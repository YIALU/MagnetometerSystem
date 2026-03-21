using System.Net.Sockets;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// TCP 设备连接实现，支持自动重连
/// </summary>
public class TcpDeviceConnection : IDeviceConnection
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _disposed;
    private int _reconnectAttempts;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _client?.Connected == true;
    public ConnectionConfig Config { get; }

    public TcpDeviceConnection(ConnectionConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return;

        try
        {
            _client = new TcpClient();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Config.ConnectTimeoutMs);

            await _client.ConnectAsync(Config.IpAddress, Config.Port, timeoutCts.Token);
            _stream = _client.GetStream();
            _reconnectAttempts = 0;

            ConnectionStateChanged?.Invoke(this, true);

            // 启动后台读取任务
            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"TCP 连接失败: {ex.Message}");
            throw;
        }
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

        _stream?.Dispose();
        _stream = null;

        if (_client != null)
        {
            _client.Close();
            _client.Dispose();
            _client = null;
        }

        ConnectionStateChanged?.Invoke(this, false);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_stream == null || !IsConnected)
            throw new InvalidOperationException("TCP 未连接");

        await _stream.WriteAsync(data, ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_stream == null || !IsConnected)
                {
                    // 尝试重连
                    if (Config.AutoReconnect)
                    {
                        await TryReconnectAsync(ct);
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }

                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);

                if (bytesRead == 0)
                {
                    // 远端关闭连接
                    ErrorOccurred?.Invoke(this, "TCP 连接被远端关闭");
                    ConnectionStateChanged?.Invoke(this, false);
                    _stream?.Dispose();
                    _stream = null;
                    _client?.Dispose();
                    _client = null;
                    continue;
                }

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                DataReceived?.Invoke(this, data);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                ErrorOccurred?.Invoke(this, $"TCP 读取错误: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
                _stream?.Dispose();
                _stream = null;
                _client?.Dispose();
                _client = null;

                if (!Config.AutoReconnect) break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"TCP 异常: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// 指数退避重连
    /// </summary>
    private async Task TryReconnectAsync(CancellationToken ct)
    {
        if (Config.MaxReconnectAttempts > 0 && _reconnectAttempts >= Config.MaxReconnectAttempts)
        {
            ErrorOccurred?.Invoke(this, $"已达到最大重连次数 ({Config.MaxReconnectAttempts})，停止重连");
            return;
        }

        _reconnectAttempts++;
        int delayMs = Math.Min(1000 * (1 << Math.Min(_reconnectAttempts, 5)), 30000); // 最长 30 秒

        ErrorOccurred?.Invoke(this, $"将在 {delayMs}ms 后尝试第 {_reconnectAttempts} 次重连...");
        await Task.Delay(delayMs, ct);

        try
        {
            _client?.Dispose();
            _client = new TcpClient();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Config.ConnectTimeoutMs);

            await _client.ConnectAsync(Config.IpAddress, Config.Port, timeoutCts.Token);
            _stream = _client.GetStream();
            _reconnectAttempts = 0;

            ConnectionStateChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"重连失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
