using System.ComponentModel;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 单通道的显示配置（偏移、颜色、可见性等）
/// </summary>
public class ChannelDisplayConfig : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>通道名称</summary>
    public string Name { get; set; } = "Channel";

    /// <summary>通道索引</summary>
    public int ChannelIndex { get; set; }
    public string Unit { get; set; } = "nT";
    private string _latestValue = "—";
    public string LatestValue
    {
        get => _latestValue;
        set { if (_latestValue == value) return; _latestValue = value; PropertyChanged?.Invoke(this, new(nameof(LatestValue))); }
    }

    /// <summary>显示偏移（仅影响图表显示，不影响原始数据和运算）</summary>
    private double _displayOffset;
    public double DisplayOffset
    {
        get => _displayOffset;
        set
        {
            if (_displayOffset != value)
            {
                _displayOffset = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayOffset)));
            }
        }
    }

    /// <summary>是否在图表中显示</summary>
    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible != value)
            {
                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }
    }

    /// <summary>曲线颜色（ARGB hex 字符串，如 "#FF2A78D6"）</summary>
    private string _colorHex = "#FF2A78D6";
    public string ColorHex
    {
        get => _colorHex;
        set { if (_colorHex == value) return; _colorHex = value; PropertyChanged?.Invoke(this, new(nameof(ColorHex))); }
    }

    /// <summary>
    /// 默认通道颜色：经色觉辨识校验的 8 色（与界面色块一致）。默认色按 index % Length 回绕分配，
    /// 超过 8 个通道时颜色会重复，靠图例中的通道名区分；需要时可在“通道”面板逐个改色。
    /// 已保存的用户配置保留原色值，不受影响。
    /// </summary>
    public static readonly string[] PresetColors =
    [
        "#FF2A78D6", // 蓝
        "#FFEB6834", // 橙
        "#FF1BAF7A", // 绿
        "#FFEDA100", // 黄
        "#FFE87BA4", // 粉
        "#FF008300", // 深绿
        "#FF4A3AA7", // 紫
        "#FFE34948", // 红
    ];

    /// <summary>
    /// 从 hex 字符串解析为 ScottPlot 可用的 ARGB 分量
    /// </summary>
    public (byte a, byte r, byte g, byte b) ParseColor()
    {
        var hex = ColorHex.TrimStart('#');
        if (hex.Length == 6)
        {
            return (255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        if (hex.Length == 8)
        {
            return (Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        return (255, 0, 0, 255); // default blue
    }

    /// <summary>创建默认的通道配置</summary>
    public static ChannelDisplayConfig[] CreateDefaults(int channelCount, string[]? channelNames = null)
    {
        var configs = new ChannelDisplayConfig[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            configs[i] = new ChannelDisplayConfig
            {
                Name = channelNames != null && i < channelNames.Length ? channelNames[i] : $"CH{i}",
                ChannelIndex = i,
                ColorHex = PresetColors[i % PresetColors.Length],
                Visible = true,
                DisplayOffset = 0,
            };
        }
        return configs;
    }
}
