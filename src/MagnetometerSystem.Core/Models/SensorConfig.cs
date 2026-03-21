namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 传感器配置
/// </summary>
public class SensorConfig
{
    /// <summary>传感器类型</summary>
    public SensorType Type { get; set; }

    /// <summary>
    /// 采样率 (Hz)。
    /// 注: 当前版本仅用于 UI 显示和会话元数据记录。
    /// 实际采样节奏由设备硬件决定，降采样/插值功能（SamplingRateAdapter）尚未实现。
    /// </summary>
    public double SampleRate { get; set; } = 10.0;

    /// <summary>
    /// 通道数覆盖值（由协议配置动态设置）。
    /// 若 > 0 则优先使用此值，否则根据传感器类型自动确定。
    /// </summary>
    public int ChannelCountOverride { get; set; }

    /// <summary>
    /// 通道名称覆盖（由协议配置动态设置）。
    /// 若非空则优先使用此值，否则根据传感器类型自动确定。
    /// </summary>
    public string[]? ChannelNamesOverride { get; set; }

    /// <summary>通道数（优先使用覆盖值，否则根据传感器类型确定）</summary>
    public int ChannelCount => ChannelCountOverride > 0 ? ChannelCountOverride : Type switch
    {
        SensorType.SingleAxisFluxgate => 1,
        SensorType.TriaxialFluxgate => 3,
        SensorType.DualTriaxialFluxgate => 6,
        SensorType.ProtonMagnetometer => 1,
        _ => 1
    };

    /// <summary>通道名称（优先使用覆盖值，否则根据传感器类型确定）</summary>
    public string[] ChannelNames => ChannelNamesOverride ?? Type switch
    {
        SensorType.SingleAxisFluxgate => ["B"],
        SensorType.TriaxialFluxgate => ["X", "Y", "Z"],
        SensorType.DualTriaxialFluxgate => ["X1", "Y1", "Z1", "X2", "Y2", "Z2"],
        SensorType.ProtonMagnetometer => ["Total"],
        _ => ["CH0"]
    };

    /// <summary>协议类型标识（如 "ASCII_CSV", "BINARY_FRAME"）</summary>
    public string ProtocolType { get; set; } = "ASCII_CSV";

    /// <summary>关联的正交度校正配置 ID（仅三轴类传感器）</summary>
    public string? OrthogonalityProfileId { get; set; }

    /// <summary>传感器序列号</summary>
    public string? SerialNumber { get; set; }

    /// <summary>允许的最大采样率 (Hz)</summary>
    public double MaxSampleRate => Type switch
    {
        SensorType.ProtonMagnetometer => 10.0,
        _ => 500.0
    };

    /// <summary>允许的最小采样率 (Hz)</summary>
    public double MinSampleRate => Type switch
    {
        SensorType.ProtonMagnetometer => 0.1,
        _ => 0.1
    };

    /// <summary>预设采样率列表</summary>
    public static readonly double[] PresetSampleRates =
        [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];

    /// <summary>验证采样率是否在合法范围内</summary>
    public bool ValidateSampleRate()
    {
        return SampleRate >= MinSampleRate && SampleRate <= MaxSampleRate;
    }
}
