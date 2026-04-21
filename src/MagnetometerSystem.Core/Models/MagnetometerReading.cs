namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 磁力仪数据读数（通用模型，适用于所有传感器类型）
/// </summary>
public class MagnetometerReading
{
    /// <summary>数据库自增 ID</summary>
    public long Id { get; set; }

    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>采集会话 ID</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>传感器类型</summary>
    public SensorType SensorType { get; set; }

    /// <summary>
    /// 各通道值 (nT)
    /// 单轴: [B]
    /// 三轴: [X, Y, Z]
    /// 双三轴: [X1, Y1, Z1, X2, Y2, Z2]
    /// 质子: [Total]
    /// </summary>
    public double[] ChannelValues { get; set; } = [];

    /// <summary>原始通道值（校正前），仅在应用校正时保存</summary>
    public double[]? OriginalChannelValues { get; set; }

    /// <summary>是否已做传感器校准（偏移+增益）</summary>
    public bool IsCalibrated { get; set; }

    /// <summary>是否已做正交度校正</summary>
    public bool IsOrthogonalityCorrected { get; set; }
}
