using System.Net;
using System.Net.Sockets;
using System.Text;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class TcpAcquisitionLoopbackTests
{
    [Fact]
    public async Task RealSocket_FragmentedFramesAndCommandsReachBothEnds()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
            var parser = new ConfigurableAsciiParser(ProtocolConfig.CreateDefaultAsciiTriaxial());
            var received = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.DataReceived += (_, bytes) => { parser.Feed(bytes, 0, bytes.Length); while (parser.TryParse(out var reading)) received.TrySetResult(reading!); };
            var accept = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var stream = peer.GetStream();
            await stream.WriteAsync("1,,3\n10,2"u8.ToArray());
            await stream.WriteAsync("0,30\n"u8.ToArray());
            Assert.Equal(new[] { 10d, 20d, 30d }, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).ChannelValues);
            var command = CommandFrameBuilder.BuildAsciiBytes(new DeviceCommand { Template = "SET_RATE {rate}", Parameters = [new() { Key = "rate", Type = CommandParameterType.Double }] }, new Dictionary<string, string> { ["rate"] = "1000" });
            await connection.SendAsync(command);
            var actual = new byte[command.Length];
            await stream.ReadExactlyAsync(actual).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("SET_RATE 1000\r\n", Encoding.UTF8.GetString(actual));
            await connection.DisconnectAsync();
            Assert.False(connection.IsConnected);
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ConcurrentSends_PreserveWholeCommandFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
            var accept = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            using var peer = await accept;
            var first = Enumerable.Repeat((byte)0x11, 131072).ToArray();
            var second = Enumerable.Repeat((byte)0x22, 131072).ToArray();
            var actual = new byte[first.Length + second.Length];
            var read = peer.GetStream().ReadExactlyAsync(actual).AsTask();
            await Task.WhenAll(connection.SendAsync(first), connection.SendAsync(second));
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(actual.SequenceEqual(first.Concat(second)) || actual.SequenceEqual(second.Concat(first)));
            await Task.WhenAll(connection.DisconnectAsync(), connection.DisconnectAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync([1]));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RetryLimit_StopsInsteadOfSpinningAfterPeerDisappears()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = true, MaxReconnectAttempts = 2, ReconnectDelayMs = 10, ConnectTimeoutMs = 100 });
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int limitReports = 0;
        connection.ErrorOccurred += (_, message) => { if (message.Contains("最大重连次数")) { Interlocked.Increment(ref limitReports); stopped.TrySetResult(); } };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            var peer = await accept;
            listener.Stop();
            peer.Dispose();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            Assert.Equal(1, limitReports);
            Assert.False(connection.IsConnected);
        }
        finally { listener.Stop(); }
    }
}
