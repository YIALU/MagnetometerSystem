using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>只附加采集元数据，不裁剪、重排或改正协议通道。</summary>
public sealed class GenericSensorAdapter(SensorConfig config) : ISensorAdapter
{
    public SensorType SensorType => SensorType.Generic;
    public SensorConfig Config { get; } = config;
    public MagnetometerReading Process(MagnetometerReading rawReading)
    {
        rawReading.SensorType = SensorType.Generic;
        return rawReading;
    }
    public string[] GetChannelNames() => Config.ChannelNames;
    public int GetChannelCount() => Config.ChannelCount;
}
