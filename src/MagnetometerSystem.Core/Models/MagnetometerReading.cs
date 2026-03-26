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

    /// <summary>总场强度 (nT)，对三轴传感器为 sqrt(X²+Y²+Z²)</summary>
    public double? TotalField { get; set; }

    /// <summary>是否已做传感器校准（偏移+增益）</summary>
    public bool IsCalibrated { get; set; }

    /// <summary>是否已做正交度校正</summary>
    public bool IsOrthogonalityCorrected { get; set; }

    /// <summary>
    /// 根据通道值计算总场（适用于三轴传感器）
    /// </summary>
    public void ComputeTotalField()
    {
        if (SensorType == SensorType.TriaxialFluxgate && ChannelValues.Length >= 3)
        {
            TotalField = Math.Sqrt(
                ChannelValues[0] * ChannelValues[0] +
                ChannelValues[1] * ChannelValues[1] +
                ChannelValues[2] * ChannelValues[2]);
        }
        else if (SensorType == SensorType.DualTriaxialFluxgate && ChannelValues.Length >= 6)
        {
            // 第一组三轴的总场
            TotalField = Math.Sqrt(
                ChannelValues[0] * ChannelValues[0] +
                ChannelValues[1] * ChannelValues[1] +
                ChannelValues[2] * ChannelValues[2]);
        }
        else if (SensorType == SensorType.SingleAxisFluxgate && ChannelValues.Length >= 1)
        {
            TotalField = Math.Abs(ChannelValues[0]);
        }
        else if (SensorType == SensorType.ProtonMagnetometer && ChannelValues.Length >= 1)
        {
            TotalField = ChannelValues[0];
        }
    }
}
