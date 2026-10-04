using System.IO.Ports;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>串口接收、写出及关闭共享 I/O 锁，避免关闭后访问端口或帧交错。</summary>
public sealed class SerialDeviceConnection : IDeviceConnection
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _ioGate = new();
    private SerialPort? _serialPort;
    private bool _disposed;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<bool>? ConnectionStateChanged;
    public bool IsConnected { get { lock (_ioGate) return _serialPort?.IsOpen == true; } }
    public ConnectionConfig Config { get; }
    public SerialDeviceConnection(ConnectionConfig config) => Config = config ?? throw new ArgumentNullException(nameof(config));

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) return;
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var port = new SerialPort
                {
                    PortName = Config.PortName, BaudRate = Config.BaudRate, DataBits = Config.DataBits,
                    StopBits = Config.StopBits switch { 1 => System.IO.Ports.StopBits.One, 1.5 => System.IO.Ports.StopBits.OnePointFive, 2 => System.IO.Ports.StopBits.Two, _ => throw new ArgumentException("停止位无效") },
                    Parity = Enum.TryParse<Parity>(Config.Parity, true, out var parity) ? parity : throw new ArgumentException("串口校验位无效"),
                    ReadBufferSize = 65536, ReceivedBytesThreshold = 1,
                    ReadTimeout = 1000, WriteTimeout = Config.SendTimeoutMs,
                };
                try
                {
                    lock (_ioGate)
                    {
                        _serialPort = port;
                        port.DataReceived += OnSerialDataReceived;
                        port.ErrorReceived += OnSerialErrorReceived;
                        port.Open();
                        // 不清空输入缓冲：Open 后到达的首帧同样必须解析和保存。
                    }
                    Raise(ConnectionStateChanged, true);
                }
                catch
                {
                    lock (_ioGate) _serialPort = null;
                    port.DataReceived -= OnSerialDataReceived;
                    port.ErrorReceived -= OnSerialErrorReceived;
                    port.Dispose();
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs args)
    {
        try
        {
            lock (_ioGate)
            {
                var port = _serialPort;
                if (!ReferenceEquals(sender, port) || port?.IsOpen != true) return;
                int count = port.BytesToRead;
                if (count == 0) return;
                var data = new byte[count];
                int read = port.Read(data, 0, data.Length);
                if (read > 0) Raise(DataReceived, read == data.Length ? data : data[..read]);
            }
        }
        catch (Exception ex)
        {
            Raise(ErrorOccurred, $"串口读取失败: {ex.Message}");
            Raise(ConnectionStateChanged, false);
        }
    }

    private void OnSerialErrorReceived(object sender, SerialErrorReceivedEventArgs args) => Raise(ErrorOccurred, $"串口硬件错误: {args.EventType}");

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        SerialPort? requestedPort;
        lock (_ioGate) requestedPort = _serialPort;
        return Task.Run(() =>
        {
            lock (_ioGate)
            {
                ct.ThrowIfCancellationRequested();
                if (requestedPort is null || !ReferenceEquals(requestedPort, _serialPort) || !requestedPort.IsOpen)
                    throw new InvalidOperationException("串口未连接或连接已改变");
                requestedPort.Write(data, 0, data.Length);
            }
        }, ct);
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task CloseAsync()
    {
        SerialPort? port;
        lock (_ioGate)
        {
            port = _serialPort;
            _serialPort = null;
            if (port != null)
            {
                port.DataReceived -= OnSerialDataReceived;
                port.ErrorReceived -= OnSerialErrorReceived;
            }
        }
        // SerialPort.Close 等待内部事件完成，不能持有事件也需要的 I/O 锁。
        if (port != null) await Task.Run(port.Dispose).ConfigureAwait(false);
        Raise(ConnectionStateChanged, false);
    }

    private void Raise<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, value); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"串口事件处理失败: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
        GC.SuppressFinalize(this);
    }
    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();
}
