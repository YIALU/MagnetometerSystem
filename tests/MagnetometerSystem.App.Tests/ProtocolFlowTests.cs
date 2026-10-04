using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class ProtocolFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConnectionVm_RealTcpAndSessionVmSaveFirstLowRateAndFinalReadings(bool invalidCorrection) => WpfTestHost.RunAsync(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"magnetometer_vm_{Guid.NewGuid():N}.db");
        var database = new DatabaseInitializer(path);
        await database.InitializeAsync();
        var bus = new DataBus();
        var storage = new SqliteStorageService(database, bus);
        var corrector = new OrthogonalityCorrector();
        var profiles = new SqliteCalibrationRepository(database);
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, profiles);
        using var chart = new RealtimeChartViewModel(bus);
        var displayReadings = new System.Collections.Concurrent.ConcurrentQueue<MagnetometerReading>();
        bus.ProcessedReadingReceived += displayReadings.Enqueue;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var vm = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, profiles)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, SampleRate = 1000000,
            IsOrthogonalityCorrectionEnabled = invalidCorrection,
            ProtocolConfig = new ProtocolConfig
            {
                FieldMappings = Enumerable.Range(0, 21).Select(i => new FieldMapping
                { Name = $"CH{i}", Unit = i == 20 ? "°C" : "V", ByteOffset = i, ChannelIndex = i }).ToList(),
            },
        };
        static byte[] Frame(int start) => Encoding.ASCII.GetBytes(string.Join(",", Enumerable.Range(start, 21)) + "\n");
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var connect = vm.ConnectCommand.ExecuteAsync(null);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            // 设备在建立连接后立即发首帧，不依赖 Connect continuation 或页面加载。
            await peer.GetStream().WriteAsync(Frame(0));
            await connect;
            await peer.GetStream().WriteAsync(Frame(1));
            // 两帧不足批量阈值，之后不再接收也必须由周期提交落库。
            await WaitForAsync(() => storage.WriteStatus.SavedReadings == 2);
            string sessionId = Assert.Single(await storage.GetSessionsAsync()).Id;
            Assert.Equal(2, (await storage.GetReadingsAsync(sessionId)).Count);
            await peer.GetStream().WriteAsync(Frame(2));
            await WaitForAsync(() => vm.ParsedReadingCount == 3);
            await vm.StopAcquisitionAsync();
            var info = Assert.Single(await storage.GetSessionsAsync());
            var saved = await storage.GetReadingsAsync(sessionId);
            Assert.Equal(3, saved.Count);
            Assert.Equal(3, info.TotalReadings);
            Assert.NotNull(info.EndedAt);
            Assert.Equal(SensorType.Generic, info.SensorType);
            Assert.Equal(ConnectionType.Tcp, info.ConnectionType);
            Assert.Equal(1000000, info.SampleRate);
            Assert.Equal(21, info.ChannelCount);
            Assert.Equal("°C", info.ChannelUnits[20]);
            Assert.Equal(Enumerable.Range(0, 21).Select(i => (double)i), saved[0].ChannelValues);
            Assert.Equal(Enumerable.Range(2, 21).Select(i => (double)i), saved[2].ChannelValues);
            Assert.All(saved, reading => Assert.False(reading.IsOrthogonalityCorrected));
            await WpfTestHost.PumpAsync();
            chart.RefreshPlot();
            Assert.Equal(3, chart.DataPointCount);
            Assert.Equal(3, displayReadings.Count);
            Assert.Equal(saved[2].ChannelValues, displayReadings.Last().ChannelValues);
            Assert.All(displayReadings, reading => Assert.False(reading.IsOrthogonalityCorrected));
            if (invalidCorrection) Assert.Contains("改正未应用", vm.LastError);
            Assert.Null(sessions.ActiveSessionId);
            Assert.Equal(0, storage.WriteStatus.PendingReadings);
        }
        finally
        {
            await vm.StopAcquisitionAsync();
            listener.Stop();
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    });

    [Fact]
    public Task ConnectionVm_RealTcpKeepsRawDataAndWaitsForFinalStorage() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bus = new DataBus();
        var raw = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStorage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorConfig? capturedConfig = null;
        bus.AcquisitionStarting += config => { capturedConfig = config; return Task.CompletedTask; };
        bus.ReadingReceived += reading => raw.TrySetResult(reading);
        bus.ProcessedReadingReceived += reading => processed.TrySetResult(reading);
        bus.AcquisitionStopping += () => { stopping.TrySetResult(); return releaseStorage.Task; };
        var vm = new ConnectionViewModel(new ConnectionFactory(), bus, new OrthogonalityCorrector(), new NoCalibrationRepository())
        {
            SelectedConnectionType = ConnectionType.Tcp,
            IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            SelectedSensorType = SensorType.ProtonMagnetometer, SampleRate = 1000,
            IsOrthogonalityCorrectionEnabled = true,
            ActiveOrthogonalityProfile = new OrthogonalityParams { Offset = [1, 1, 1] },
            FirstOrthogonalityChannelsText = "0,1,2",
        };
        vm.ProtocolConfig.FieldMappings.Add(new FieldMapping { Name = "T", Unit = "°C", ChannelIndex = 3, ByteOffset = 3 });
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected, vm.LastError);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(SensorType.Generic, capturedConfig!.Type);
            Assert.Equal(4, capturedConfig.ChannelCount);
            Assert.Equal("°C", capturedConfig.ChannelUnits[3]);
            // 修改编辑草稿不影响当前会话已冻结的解析规则。
            vm.ProtocolConfig.FieldMappings[0].Scale = 100;
            await peer.GetStream().WriteAsync("0,,0,0\n10,20,"u8.ToArray());
            await peer.GetStream().WriteAsync("30,24\n"u8.ToArray());
            var original = await raw.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var corrected = await processed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { 10d, 20d, 30d, 24d }, original.ChannelValues);
            Assert.Null(original.OriginalChannelValues);
            Assert.Equal(new[] { 9d, 19d, 29d, 24d }, corrected.ChannelValues);
            Assert.Equal(original.ChannelValues, corrected.OriginalChannelValues);
            await WpfTestHost.PumpAsync();
            Assert.Equal(1, vm.ParseErrorCount);
            var stop = vm.StopAcquisitionAsync();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stop.IsCompleted);
            Assert.True(vm.IsAcquiring);
            releaseStorage.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsAcquiring);
            Assert.Null(bus.CurrentConnection);
        }
        finally
        {
            releaseStorage.TrySetResult();
            await vm.StopAcquisitionAsync();
            listener.Stop();
        }
    });

    [Fact]
    public Task DeviceCommandVm_WritesActualBytesAndWaitsForFragmentedAck() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig());
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var command = ProtocolConfig.CreateCtmbs3X2000().Commands[0].Commands[1];
            vm.SetProtocolCommands([new CommandGroup { Name = "CTMBS", Commands = [command] }]);
            vm.SelectedCommand = command;
            var expected = Ctmbs3X2000FrameBuilder.BuildRequestBytes(command, command.Parameters.ToDictionary(p => p.Key, p => p.DefaultValue));
            await vm.SendSelectedCommandCommand.ExecuteAsync(null);
            var transmitted = new byte[expected.Length];
            await peer.GetStream().ReadExactlyAsync(transmitted).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(expected, transmitted);
            Assert.Contains("不代表设备执行成功", vm.WriteStatus);
            Assert.DoesNotContain("收到设备 ACK", vm.ResponseStatus);
            await peer.GetStream().WriteAsync("$a"u8.ToArray());
            await peer.GetStream().WriteAsync("ck\n"u8.ToArray());
            await WaitForAsync(() => vm.ResponseStatus.Contains("收到设备 ACK"));
            await connection.DisconnectAsync();
            await WpfTestHost.PumpAsync();
            Assert.False(vm.IsConnected);
            await vm.SendSelectedCommandCommand.ExecuteAsync(null);
            Assert.Contains("写出失败", vm.WriteStatus);
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    [Fact]
    public Task DeviceCommandVm_UnrelatedTelemetryDoesNotConfirmCommandExecution() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig()) { ResponseTimeoutMs = 150 };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept;
            vm.FreeCommandText = "SET_RATE 1000";
            await vm.SendFreeCommandCommand.ExecuteAsync(null);
            var received = new byte[15];
            await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("SET_RATE 1000\r\n", Encoding.ASCII.GetString(received));
            await peer.GetStream().WriteAsync("1,2,3\n"u8.ToArray());
            await WaitForAsync(() => vm.ResponseStatus.Contains("超时"));
            Assert.Contains("执行结果未知", vm.ResponseStatus);
            vm.FreeIsHexMode = true;
            Assert.Equal("None", vm.FreeLineEnding);
            vm.FreeCommandText = "01 02";
            vm.FreeLineEnding = "LF";
            await vm.SendFreeCommandCommand.ExecuteAsync(null);
            var hexCommand = new byte[3];
            await peer.GetStream().ReadExactlyAsync(hexCommand).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new byte[] { 1, 2, 10 }, hexCommand);
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ready(), "等待界面状态更新超时");
    }

    private sealed class EmptyCommandConfig : IAppConfigService
    {
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value) => Task.CompletedTask;
        public Task<MagnetometerSystem.Infrastructure.Configuration.AppSettings> LoadSettingsAsync() => Task.FromResult(new MagnetometerSystem.Infrastructure.Configuration.AppSettings());
        public Task SaveSettingsAsync(MagnetometerSystem.Infrastructure.Configuration.AppSettings settings) => Task.CompletedTask;
    }

    private sealed class NoCalibrationRepository : ICalibrationRepository
    {
        public Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile) => Task.CompletedTask;
        public Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(string? sensorSerial = null) => Task.FromResult<IReadOnlyList<OrthogonalityParams>>([]);
        public Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id) => Task.FromResult<OrthogonalityParams?>(null);
        public Task DeleteOrthogonalityProfileAsync(string id) => Task.CompletedTask;
        public Task SaveCalibrationProfileAsync(CalibrationParams profile) => Task.CompletedTask;
        public Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(SensorType? sensorType = null) => Task.FromResult<IReadOnlyList<CalibrationParams>>([]);
        public Task DeleteCalibrationProfileAsync(string id) => Task.CompletedTask;
        public Task<int> SaveOrthogonalityCalibrationAsync(OrthogonalityCalibrationRecord record) => Task.FromResult(1);
        public Task<List<OrthogonalityCalibrationRecord>> GetOrthogonalityHistoryAsync(string deviceId) => Task.FromResult(new List<OrthogonalityCalibrationRecord>());
    }
}
