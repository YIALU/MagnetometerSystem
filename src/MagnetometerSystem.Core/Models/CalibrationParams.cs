namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 通用传感器校准参数（偏移+增益）
/// </summary>
public class CalibrationParams
{
    /// <summary>配置 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>配置名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>传感器类型</summary>
    public SensorType SensorType { get; set; }

    /// <summary>关联传感器序列号</summary>
    public string? SensorSerial { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>各通道偏移量</summary>
    public double[] OffsetValues { get; set; } = [];

    /// <summary>各通道增益系数</summary>
    public double[] GainValues { get; set; } = [];

    /// <summary>备注</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// 对原始值应用校准: calibrated = (raw - offset) * gain
    /// </summary>
    public double[] Apply(double[] rawValues)
    {
        var result = new double[rawValues.Length];
        for (int i = 0; i < rawValues.Length; i++)
        {
            double offset = i < OffsetValues.Length ? OffsetValues[i] : 0;
            double gain = i < GainValues.Length ? GainValues[i] : 1;
            result[i] = (rawValues[i] - offset) * gain;
        }
        return result;
    }
}
