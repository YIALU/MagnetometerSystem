using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 三轴磁通门适配器
/// </summary>
public class TriaxialFluxgateAdapter : ISensorAdapter
{
    public SensorType SensorType => SensorType.TriaxialFluxgate;
    public SensorConfig Config { get; }

    public TriaxialFluxgateAdapter(SensorConfig config)
    {
        Config = config;
    }

    public MagnetometerReading Process(MagnetometerReading rawReading)
    {
        rawReading.SensorType = SensorType;
        if (rawReading.ChannelValues.Length >= 3)
        {
            double x = rawReading.ChannelValues[0];
            double y = rawReading.ChannelValues[1];
            double z = rawReading.ChannelValues[2];
            rawReading.TotalField = Math.Sqrt(x * x + y * y + z * z);
        }
        return rawReading;
    }

    public string[] GetChannelNames() => ["X", "Y", "Z"];
    public int GetChannelCount() => 3;
}
