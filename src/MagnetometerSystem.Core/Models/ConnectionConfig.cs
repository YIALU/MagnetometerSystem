namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 连接类型枚举
/// </summary>
public enum ConnectionType
{
    Serial,
    Tcp
}

/// <summary>
/// 设备连接配置
/// </summary>
public class ConnectionConfig
{
    /// <summary>连接类型</summary>
    public ConnectionType Type { get; set; } = ConnectionType.Serial;

    // ---- 串口参数 ----

    /// <summary>串口名称（如 COM3）</summary>
    public string PortName { get; set; } = "COM1";

    /// <summary>波特率</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>数据位</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位 (1, 1.5, 2)</summary>
    public double StopBits { get; set; } = 1;

    /// <summary>校验位 (None, Odd, Even)</summary>
    public string Parity { get; set; } = "None";

    // ---- TCP 参数 ----

    /// <summary>远程 IP 地址</summary>
    public string IpAddress { get; set; } = "192.168.1.100";

    /// <summary>远程端口号</summary>
    public int Port { get; set; } = 5000;

    /// <summary>连接超时 (毫秒)</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>是否启用自动重连</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>重连最大重试次数（0 = 无限）</summary>
    public int MaxReconnectAttempts { get; set; } = 0;
}
