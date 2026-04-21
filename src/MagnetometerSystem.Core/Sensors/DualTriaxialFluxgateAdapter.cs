using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 双三轴磁通门适配器，支持梯度计算
/// </summary>
public class DualTriaxialFluxgateAdapter : ISensorAdapter
{
    public SensorType SensorType => SensorType.DualTriaxialFluxgate;
    public SensorConfig Config { get; }

    /// <summary>两组传感器的基线距离 (米)，用于梯度计算</summary>
    public double BaselineDistance { get; set; } = 1.0;

    public DualTriaxialFluxgateAdapter(SensorConfig config)
    {
        Config = config;
    }

    public MagnetometerReading Process(MagnetometerReading rawReading)
    {
        rawReading.SensorType = SensorType;
        return rawReading;
    }

    /// <summary>
    /// 计算两组传感器间的总场梯度 (nT/m)
    /// </summary>
    public double? ComputeGradient(MagnetometerReading reading)
    {
        if (reading.ChannelValues.Length < 6 || BaselineDistance <= 0)
            return null;

        double x1 = reading.ChannelValues[0], y1 = reading.ChannelValues[1], z1 = reading.ChannelValues[2];
        double x2 = reading.ChannelValues[3], y2 = reading.ChannelValues[4], z2 = reading.ChannelValues[5];

        double total1 = Math.Sqrt(x1 * x1 + y1 * y1 + z1 * z1);
        double total2 = Math.Sqrt(x2 * x2 + y2 * y2 + z2 * z2);

        return (total1 - total2) / BaselineDistance;
    }

    public string[] GetChannelNames() => ["X1", "Y1", "Z1", "X2", "Y2", "Z2"];
    public int GetChannelCount() => 6;
}
