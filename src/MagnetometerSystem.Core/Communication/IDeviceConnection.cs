using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 设备连接抽象接口
/// </summary>
public interface IDeviceConnection : IAsyncDisposable
{
    /// <summary>收到原始数据时触发</summary>
    event EventHandler<byte[]> DataReceived;

    /// <summary>发生通信错误时触发</summary>
    event EventHandler<string> ErrorOccurred;

    /// <summary>连接状态变化时触发</summary>
    event EventHandler<bool> ConnectionStateChanged;

    /// <summary>当前是否已连接</summary>
    bool IsConnected { get; }

    /// <summary>连接配置</summary>
    ConnectionConfig Config { get; }

    /// <summary>异步连接设备</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>异步断开设备</summary>
    Task DisconnectAsync();

    /// <summary>异步发送数据（用于发送命令到设备）</summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);
}
