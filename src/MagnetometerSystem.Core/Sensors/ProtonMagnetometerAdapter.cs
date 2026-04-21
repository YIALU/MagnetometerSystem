using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 质子磁力仪适配器
/// </summary>
public class ProtonMagnetometerAdapter : ISensorAdapter
{
    public SensorType SensorType => SensorType.ProtonMagnetometer;
    public SensorConfig Config { get; }

    public ProtonMagnetometerAdapter(SensorConfig config)
    {
        Config = config;
    }

    public MagnetometerReading Process(MagnetometerReading rawReading)
    {
        rawReading.SensorType = SensorType;
        return rawReading;
    }

    public string[] GetChannelNames() => ["Total"];
    public int GetChannelCount() => 1;
}
