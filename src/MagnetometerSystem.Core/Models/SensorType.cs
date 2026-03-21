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
    ProtonMagnetometer
}
