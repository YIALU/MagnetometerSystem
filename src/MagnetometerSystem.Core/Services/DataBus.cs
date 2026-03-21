using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Services;

/// <summary>
/// 实时数据总线：发布-订阅模式，解耦数据源与消费者
/// </summary>
public class DataBus
{
    /// <summary>新的读数到达时触发</summary>
    public event Action<MagnetometerReading>? ReadingReceived;

    /// <summary>采集开始</summary>
    public event Action<SensorConfig>? AcquisitionStarted;

    /// <summary>采集停止</summary>
    public event Action? AcquisitionStopped;

    public void PublishReading(MagnetometerReading reading)
    {
        ReadingReceived?.Invoke(reading);
    }

    public void PublishAcquisitionStarted(SensorConfig config)
    {
        AcquisitionStarted?.Invoke(config);
    }

    public void PublishAcquisitionStopped()
    {
        AcquisitionStopped?.Invoke();
    }
}
