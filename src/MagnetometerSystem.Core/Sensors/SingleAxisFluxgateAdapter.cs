using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 单轴磁通门适配器
/// </summary>
public class SingleAxisFluxgateAdapter : ISensorAdapter
{
    public SensorType SensorType => SensorType.SingleAxisFluxgate;
    public SensorConfig Config { get; }

    public SingleAxisFluxgateAdapter(SensorConfig config)
    {
        Config = config;
    }

    public MagnetometerReading Process(MagnetometerReading rawReading)
    {
        rawReading.SensorType = SensorType;
        return rawReading;
    }

    public string[] GetChannelNames() => ["B"];
    public int GetChannelCount() => 1;
}
