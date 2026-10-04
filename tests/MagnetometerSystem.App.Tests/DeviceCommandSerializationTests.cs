using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using AppSettings = MagnetometerSystem.Infrastructure.Configuration.AppSettings;

namespace MagnetometerSystem.App.Tests;

public class DeviceCommandSerializationTests
{
    [Fact]
    public Task EachCtmbsCommandWaitsForItsOwnAckBeforeTheNextCommandWrites() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await TcpFixture.CreateAsync();
        var commands = ProtocolConfig.CreateCtmbs3X2000().Commands.SelectMany(g => g.Commands).ToArray();
        var firstCommand = commands.Single(c => c.Template == "stp");
        var secondCommand = commands.Single(c => c.Template == "dat");
        var first = fixture.SendSelected(firstCommand);
        await fixture.ReadCommandAsync(firstCommand);
        await WaitForWriteAsync(fixture.Vm);

        var second = fixture.SendSelected(secondCommand, "5");
        // SendAsync is counted synchronously before the real TCP write. Both calls
        // have entered the VM, so this assertion needs no timing-based quiet period.
        Assert.Equal(1, fixture.Connection.SendCalls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.True(fixture.Vm.IsSending);

        await fixture.Stream.WriteAsync("$ack\n"u8.ToArray());
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ReadCommandAsync(secondCommand, "5");
        await WaitForWriteAsync(fixture.Vm);
        Assert.Equal(2, fixture.Connection.SendCalls);
        Assert.False(second.IsCompleted);
        Assert.True(fixture.Vm.IsSending);
        Assert.Contains("等待协议响应", fixture.Vm.ResponseStatus);

        // The first ACK was already consumed; the second request also needs a
        // complete ACK of its own, even though both requests use the same token.
        await fixture.Stream.WriteAsync("$a"u8.ToArray());
        await fixture.Connection.WaitForReceivedAsync(7);
        Assert.False(second.IsCompleted);
        await fixture.Stream.WriteAsync("ck\n"u8.ToArray());
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Vm.IsSending);
        Assert.Contains("收到设备 ACK", fixture.Vm.ResponseStatus);
    });

    [Fact]
    public Task ActualResponseTimeoutReleasesQueuedCommandWithoutConfirmingIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await TcpFixture.CreateAsync();
        fixture.Vm.ResponseTimeoutMs = 2000;
        var statuses = new List<string>();
        fixture.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceCommandViewModel.ResponseStatus))
                statuses.Add(fixture.Vm.ResponseStatus);
        };
        var firstCommand = AsciiCommand("FIRST");
        var secondCommand = AsciiCommand("SECOND");
        var first = fixture.SendSelected(firstCommand);
        await fixture.ReadCommandAsync(firstCommand);
        await WaitForWriteAsync(fixture.Vm);
        fixture.Vm.ResponseTimeoutMs = 20000;
        var second = fixture.SendSelected(secondCommand);
        Assert.Equal(1, fixture.Connection.SendCalls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        // Exercise the real timeout; no synthetic cancellation or guessed delay.
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.ReadCommandAsync(secondCommand);
        await WaitForWriteAsync(fixture.Vm);
        Assert.Contains(statuses, status => status.Contains("超时") && status.Contains("执行结果未知"));
        Assert.Equal(2, fixture.Connection.SendCalls);
        Assert.False(second.IsCompleted);
        await fixture.Stream.WriteAsync("ACK\n"u8.ToArray());
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("收到匹配响应", fixture.Vm.ResponseStatus);
        Assert.False(fixture.Vm.IsSending);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DisconnectOrDisposeCancelsPendingWaitAndDoesNotWriteQueuedFreeCommand(bool dispose) => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await TcpFixture.CreateAsync();
        var command = AsciiCommand("FIRST");
        var first = fixture.SendSelected(command);
        await fixture.ReadCommandAsync(command);
        await WaitForWriteAsync(fixture.Vm);
        fixture.Vm.FreeCommandText = "MUST_NOT_WRITE";
        fixture.Vm.FreeLineEnding = "None";
        var queued = fixture.SendFree();
        Assert.Equal(1, fixture.Connection.SendCalls);
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);

        if (dispose) fixture.Vm.Dispose();
        else await fixture.Connection.DisconnectAsync();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Connection.SendCalls);
        Assert.False(fixture.Vm.IsSending);
        Assert.Contains("写出失败", fixture.Vm.WriteStatus);

        if (dispose)
        {
            await fixture.SendSelected(command).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Connection.SendCalls);
        }
        else
        {
            // EOF on the actual old peer also proves the queued free bytes were
            // never written before disconnect. A new connection can send again.
            Assert.Equal(0, await fixture.Stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await fixture.ReconnectAsync();
            var resumed = fixture.SendSelected(AsciiCommand("AFTER_RECONNECT"));
            await fixture.ReadCommandAsync(AsciiCommand("AFTER_RECONNECT"));
            Assert.False(resumed.IsCompleted);
            await fixture.Stream.WriteAsync("ACK\n"u8.ToArray());
            await resumed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.Connection.SendCalls);
            Assert.Contains("收到匹配响应", fixture.Vm.ResponseStatus);
        }
    });

    private static DeviceCommand AsciiCommand(string text) => new()
    { Name = text, Template = text, AppendNewline = false, ExpectedResponse = "ACK\\n" };

    private static async Task WaitForWriteAsync(DeviceCommandViewModel vm)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, PropertyChangedEventArgs e)
        {
            if (vm.WriteStatus.StartsWith("已写出")) written.TrySetResult();
        }
        vm.PropertyChanged += Check;
        try
        {
            Check(null, new PropertyChangedEventArgs(null));
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { vm.PropertyChanged -= Check; }
    }

    private sealed class TcpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly DataBus _bus = new();
        private readonly List<Task> _sends = [];
        private TcpClient _peer = null!;
        public ObservedTcpConnection Connection { get; private set; } = null!;
        public DeviceCommandViewModel Vm { get; private set; } = null!;
        public NetworkStream Stream => _peer.GetStream();

        public static async Task<TcpFixture> CreateAsync()
        {
            var fixture = new TcpFixture();
            fixture._listener.Start();
            fixture.Connection = new ObservedTcpConnection(new ConnectionConfig
            {
                IpAddress = "127.0.0.1", Port = ((IPEndPoint)fixture._listener.LocalEndpoint).Port,
                AutoReconnect = false,
            });
            fixture.Vm = new DeviceCommandViewModel(fixture._bus, new EmptyConfig()) { ResponseTimeoutMs = 20000 };
            fixture._bus.PublishConnectionChanged(fixture.Connection);
            await fixture.ReconnectAsync();
            return fixture;
        }

        public async Task ReconnectAsync()
        {
            _peer?.Dispose();
            var accept = _listener.AcceptTcpClientAsync();
            await Connection.ConnectAsync();
            _peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task SendSelected(DeviceCommand command, string? mode = null)
        {
            Vm.SelectedCommand = command;
            if (mode != null) Vm.CurrentParameters.Single(p => p.Definition.Key == "mode").Value = mode;
            var task = Vm.SendSelectedCommandCommand.ExecuteAsync(null);
            _sends.Add(task);
            return task;
        }

        public Task SendFree()
        {
            var task = Vm.SendFreeCommandCommand.ExecuteAsync(null);
            _sends.Add(task);
            return task;
        }

        public async Task ReadCommandAsync(DeviceCommand command, string? mode = null)
        {
            var values = command.Parameters.ToDictionary(p => p.Key, p => p.DefaultValue);
            if (mode != null) values["mode"] = mode;
            byte[] expected = command.Encoding == CommandEncoding.CtmbsRequest
                ? Ctmbs3X2000FrameBuilder.BuildRequestBytes(command, values)
                : CommandFrameBuilder.BuildAsciiBytes(command, values);
            var received = new byte[expected.Length];
            await Stream.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, received);
        }

        public async ValueTask DisposeAsync()
        {
            _bus.PublishConnectionChanged(null);
            Vm.Dispose();
            await Connection.DisposeAsync();
            _peer?.Dispose();
            _listener.Stop();
            await Task.WhenAll(_sends).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // The byte transport and peer are real TCP. The wrapper records entry into
    // SendAsync synchronously and provides a receive-processing barrier only.
    private sealed class ObservedTcpConnection : IDeviceConnection
    {
        private readonly TcpDeviceConnection _inner;
        private int _sendCalls;
        private long _receivedBytes;
        private event Action? Received;
        public int SendCalls => Volatile.Read(ref _sendCalls);
        public bool IsConnected => _inner.IsConnected;
        public ConnectionConfig Config => _inner.Config;
        public event EventHandler<byte[]>? DataReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<bool>? ConnectionStateChanged;

        public ObservedTcpConnection(ConnectionConfig config)
        {
            _inner = new TcpDeviceConnection(config);
            _inner.DataReceived += (_, bytes) =>
            {
                DataReceived?.Invoke(this, bytes);
                Interlocked.Add(ref _receivedBytes, bytes.Length);
                Received?.Invoke();
            };
            _inner.ErrorOccurred += (_, message) => ErrorOccurred?.Invoke(this, message);
            _inner.ConnectionStateChanged += (_, connected) => ConnectionStateChanged?.Invoke(this, connected);
        }

        public async Task WaitForReceivedAsync(long count)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check() { if (Interlocked.Read(ref _receivedBytes) >= count) received.TrySetResult(); }
            Received += Check;
            try { Check(); await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Received -= Check; }
        }

        public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
        public Task DisconnectAsync() => _inner.DisconnectAsync();
        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sendCalls);
            return _inner.SendAsync(data, ct);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class EmptyConfig : IAppConfigService
    {
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
    }
}
