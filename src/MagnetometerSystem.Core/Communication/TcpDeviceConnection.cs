using System.Net.Sockets;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>TCP 字节流连接。生命周期与写出分别串行化；断开会取消在途 I/O。</summary>
public sealed class TcpDeviceConnection : IDeviceConnection
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _runCts;
    private Task? _readTask;
    private int _connected;
    private bool _disposed;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<bool>? ConnectionStateChanged;
    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public ConnectionConfig Config { get; }

    public TcpDeviceConnection(ConnectionConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(config.IpAddress)
            || config.ConnectTimeoutMs <= 0 || config.SendTimeoutMs <= 0
            || config.ReconnectDelayMs <= 0 || config.MaxReconnectAttempts < 0)
            throw new ArgumentException("TCP 地址、端口或超时参数无效", nameof(config));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) return;
            if (_readTask is { IsCompleted: false })
                throw new InvalidOperationException("TCP 正在重连，请先停止当前连接");
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCts.Token);
            try
            {
                await OpenClientAsync(linked.Token).ConfigureAwait(false);
                var token = _runCts.Token;
                _readTask = Task.Run(() => ReadLoopAsync(token), CancellationToken.None);
            }
            catch
            {
                CloseClient();
                _runCts.Dispose();
                _runCts = null;
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task OpenClientAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Config.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(Config.IpAddress, Config.Port, timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _client = client;
            _stream = client.GetStream();
            SetConnected(true);
        }
        catch { client.Dispose(); throw; }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var lifetime = _runCts;
        var requestedStream = _stream;
        if (lifetime is null || !IsConnected) throw new InvalidOperationException("TCP 未连接");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(Config.SendTimeoutMs);
        await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream is null || !IsConnected || !ReferenceEquals(lifetime, _runCts) || !ReferenceEquals(stream, requestedStream))
                throw new InvalidOperationException("TCP 连接已改变，命令未发送");
            await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopAsync()
    {
        var lifetime = _runCts;
        if (lifetime != null) await lifetime.CancelAsync().ConfigureAwait(false);
        CloseClient();
        if (_readTask != null)
        {
            try { await _readTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _readTask = null;
        }
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { _runCts = null; lifetime?.Dispose(); }
        finally { _sendGate.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var attempts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream is null)
                {
                    if (!Config.AutoReconnect) break;
                    if (Config.MaxReconnectAttempts > 0 && attempts >= Config.MaxReconnectAttempts)
                    {
                        ReportError($"已达到最大重连次数 ({Config.MaxReconnectAttempts})，请停止后重新连接");
                        break;
                    }
                    int delay = (int)Math.Min((long)Config.ReconnectDelayMs * (1L << Math.Min(attempts, 5)), 30000);
                    attempts++;
                    ReportError($"TCP 断开，{delay}ms 后第 {attempts} 次重连");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    try { await OpenClientAsync(ct).ConfigureAwait(false); attempts = 0; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) { ReportError($"TCP 重连失败: {ex.Message}"); }
                    continue;
                }
                try
                {
                    int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) throw new IOException("远端关闭连接");
                    RaiseSafely(DataReceived, buffer[..count]);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    if (!ct.IsCancellationRequested) ReportError($"TCP 读取中断: {ex.Message}");
                    CloseClient();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { CloseClient(); }
    }

    private void CloseClient()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        SetConnected(false);
    }
    private void SetConnected(bool connected)
    {
        if (Interlocked.Exchange(ref _connected, connected ? 1 : 0) != (connected ? 1 : 0))
            RaiseSafely(ConnectionStateChanged, connected);
    }
    private void ReportError(string message) => RaiseSafely(ErrorOccurred, message);
    private void RaiseSafely<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, value); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"TCP 事件处理失败: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
        GC.SuppressFinalize(this);
    }
}
