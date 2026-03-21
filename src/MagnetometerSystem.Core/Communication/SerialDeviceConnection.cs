using System.IO.Ports;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 串口设备连接实现
/// </summary>
public class SerialDeviceConnection : IDeviceConnection
{
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
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
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                ReadBufferSize = 65536,
            };

            _serialPort.Open();
            _serialPort.DiscardInBuffer();

            ConnectionStateChanged?.Invoke(this, true);

            // 启动后台读取任务
            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"串口连接失败: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (_readCts != null)
        {
            await _readCts.CancelAsync();
            if (_readTask != null)
            {
                try { await _readTask; }
                catch (OperationCanceledException) { }
            }
            _readCts.Dispose();
            _readCts = null;
            _readTask = null;
        }

        if (_serialPort?.IsOpen == true)
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

        _serialPort?.Dispose();
        _serialPort = null;

        ConnectionStateChanged?.Invoke(this, false);
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_serialPort?.IsOpen != true)
            throw new InvalidOperationException("串口未连接");

        _serialPort.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_serialPort?.IsOpen != true)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                var stream = _serialPort.BaseStream;
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);

                if (bytesRead > 0)
                {
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);
                    DataReceived?.Invoke(this, data);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // 设备可能被拔出
                ErrorOccurred?.Invoke(this, $"串口读取错误（设备可能已断开）: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                ErrorOccurred?.Invoke(this, $"串口访问被拒绝: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"串口读取异常: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
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
