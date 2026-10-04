namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 传感器类型枚举
/// </summary>
public enum SensorType
{
    /// <summary>单轴磁通门</summary>
    SingleAxisFluxgate,

    /// <summary>三轴磁通门</summary>
    TriaxialFluxgate,

    /// <summary>双三轴磁通门</summary>
    DualTriaxialFluxgate,

    /// <summary>质子磁力仪</summary>
    ProtonMagnetometer,

    /// <summary>通用协议采集；通道由协议定义，不受设备类型约束</summary>
    Generic
}
