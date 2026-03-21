using System.IO.Ports;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 串口设备连接实现（使用 DataReceived 事件模式）
/// </summary>
public class SerialDeviceConnection : IDeviceConnection
{
    private SerialPort? _serialPort;
    private bool _disposed;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _serialPort?.IsOpen == true;
    public ConnectionConfig Config { get; }

    public SerialDeviceConnection(ConnectionConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return Task.CompletedTask;

        try
        {
            _serialPort = new SerialPort
            {
                PortName = Config.PortName,
                BaudRate = Config.BaudRate,
                DataBits = Config.DataBits,
                StopBits = Config.StopBits switch
                {
                    1.0 => System.IO.Ports.StopBits.One,
                    1.5 => System.IO.Ports.StopBits.OnePointFive,
                    2.0 => System.IO.Ports.StopBits.Two,
                    _ => System.IO.Ports.StopBits.One
                },
                Parity = Config.Parity?.ToLower() switch
                {
                    "odd" => System.IO.Ports.Parity.Odd,
                    "even" => System.IO.Ports.Parity.Even,
                    "mark" => System.IO.Ports.Parity.Mark,
                    "space" => System.IO.Ports.Parity.Space,
                    _ => System.IO.Ports.Parity.None
                },
                ReadBufferSize = 65536,
                ReceivedBytesThreshold = 1,
            };

            _serialPort.DataReceived += OnSerialDataReceived;
            _serialPort.ErrorReceived += OnSerialErrorReceived;

            _serialPort.Open();
            _serialPort.DiscardInBuffer();

            ConnectionStateChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"串口连接失败: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_serialPort?.IsOpen != true)
                return;

            int bytesToRead = _serialPort.BytesToRead;
            if (bytesToRead <= 0)
                return;

            var buffer = new byte[bytesToRead];
            int bytesRead = _serialPort.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                if (bytesRead < buffer.Length)
                {
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);
                    DataReceived?.Invoke(this, data);
                }
                else
                {
                    DataReceived?.Invoke(this, buffer);
                }
            }
        }
        catch (IOException ex)
        {
            ErrorOccurred?.Invoke(this, $"串口读取错误（设备可能已断开）: {ex.Message}");
            ConnectionStateChanged?.Invoke(this, false);
        }
        catch (InvalidOperationException ex)
        {
            ErrorOccurred?.Invoke(this, $"串口已关闭: {ex.Message}");
            ConnectionStateChanged?.Invoke(this, false);
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorOccurred?.Invoke(this, $"串口访问被拒绝: {ex.Message}");
            ConnectionStateChanged?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"串口读取异常: {ex.Message}");
        }
    }

    private void OnSerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        ErrorOccurred?.Invoke(this, $"串口硬件错误: {e.EventType}");
    }

    public Task DisconnectAsync()
    {
        if (_serialPort != null)
        {
            _serialPort.DataReceived -= OnSerialDataReceived;
            _serialPort.ErrorReceived -= OnSerialErrorReceived;

            if (_serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Close();
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"串口关闭异常: {ex.Message}");
                }
            }

            _serialPort.Dispose();
            _serialPort = null;
        }

        ConnectionStateChanged?.Invoke(this, false);
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_serialPort?.IsOpen != true)
            throw new InvalidOperationException("串口未连接");

        _serialPort.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 获取系统可用的串口列表
    /// </summary>
    public static string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames();
    }
}
