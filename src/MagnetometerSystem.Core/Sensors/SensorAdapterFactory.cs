using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 传感器适配器工厂
/// </summary>
public class SensorAdapterFactory
{
    /// <summary>
    /// 根据传感器配置创建对应的适配器
    /// </summary>
    public static ISensorAdapter Create(SensorConfig config)
    {
        return config.Type switch
        {
            SensorType.SingleAxisFluxgate => new SingleAxisFluxgateAdapter(config),
            SensorType.TriaxialFluxgate => new TriaxialFluxgateAdapter(config),
            SensorType.DualTriaxialFluxgate => new DualTriaxialFluxgateAdapter(config),
            SensorType.ProtonMagnetometer => new ProtonMagnetometerAdapter(config),
            _ => throw new ArgumentException($"不支持的传感器类型: {config.Type}")
        };
    }
}
