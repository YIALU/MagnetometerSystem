using System.IO.Ports;
using System.Text;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public sealed class SerialPairFactAttribute : FactAttribute
{
    public SerialPairFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_RX_PORT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_TX_PORT")))
            Skip = "需要互连串口对：设置 MAGNETOMETER_TEST_RX_PORT 与 MAGNETOMETER_TEST_TX_PORT；未验证实体串口。";
    }
}

public class OptionalSerialLoopbackTests
{
    [SerialPairFact]
    public async Task ConnectedSerialPair_ReceivesDataAndWritesExactCommandBytes()
    {
        var rx = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_RX_PORT")!;
        var tx = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_TX_PORT")!;
        Assert.NotEqual(rx, tx);
        int baud = int.TryParse(Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_BAUD_RATE"), out var configured) ? configured : 115200;
        await using var connection = new SerialDeviceConnection(new ConnectionConfig { PortName = rx, BaudRate = baud });
        using var peer = new SerialPort(tx, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 3000, WriteTimeout = 3000 };
        var received = new List<byte>();
        var complete = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DataReceived += (_, bytes) => { lock (received) { received.AddRange(bytes); if (received.Count >= 7) complete.TrySetResult(received.ToArray()); } };
        peer.Open();
        await connection.ConnectAsync();
        peer.Write("1,2,3\r\n");
        Assert.Equal("1,2,3\r\n", Encoding.ASCII.GetString(await complete.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        await connection.SendAsync("ACK"u8.ToArray());
        var reply = await Task.Run(() => new[] { (byte)peer.ReadByte(), (byte)peer.ReadByte(), (byte)peer.ReadByte() });
        Assert.Equal("ACK"u8.ToArray(), reply);
        await connection.DisconnectAsync();
        Assert.False(connection.IsConnected);
    }
}
