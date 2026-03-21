using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Sensors;

/// <summary>
/// 传感器适配器接口：负责将解析后的原始通道数据适配为特定传感器类型的逻辑处理
/// </summary>
public interface ISensorAdapter
{
    /// <summary>传感器类型</summary>
    SensorType SensorType { get; }

    /// <summary>传感器配置</summary>
    SensorConfig Config { get; }

    /// <summary>
    /// 处理一条原始读数，执行传感器特有的逻辑（如计算总场、梯度等）
    /// </summary>
    MagnetometerReading Process(MagnetometerReading rawReading);

    /// <summary>
    /// 获取该传感器类型的通道名称
    /// </summary>
    string[] GetChannelNames();

    /// <summary>
    /// 获取通道数
    /// </summary>
    int GetChannelCount();
}
