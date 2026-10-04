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

    /// <summary>曲线颜色（ARGB hex 字符串，如 "#FF0000FF"）</summary>
    private string _colorHex = "#FF0000FF";
    public string ColorHex
    {
        get => _colorHex;
        set { if (_colorHex == value) return; _colorHex = value; PropertyChanged?.Invoke(this, new(nameof(ColorHex))); }
    }

    /// <summary>
    /// 预设颜色列表。默认色按 index % Length 回绕分配，因此长度决定了
    /// "多少个通道之后开始出现同色"。21 通道协议（磁梯度数采卡-pt）下
    /// 8 色会让 CH0/CH8/CH16 撞色，故扩到 24 色。
    /// 前 8 个保持原有顺序和取值，已保存的用户配置和既有截图不受影响。
    /// </summary>
    public static readonly string[] PresetColors =
    [
        "#FF0000FF", // Blue
        "#FFFF0000", // Red
        "#FF008000", // Green
        "#FFFF8C00", // Orange
        "#FF800080", // Purple
        "#FF00FFFF", // Cyan
        "#FFFF00FF", // Magenta
        "#FFB8860B", // DarkGoldenrod
        "#FF1E90FF", // DodgerBlue
        "#FFDC143C", // Crimson
        "#FF2E8B57", // SeaGreen
        "#FFFF6347", // Tomato
        "#FF9932CC", // DarkOrchid
        "#FF008B8B", // DarkCyan
        "#FFC71585", // MediumVioletRed
        "#FF808000", // Olive
        "#FF4169E1", // RoyalBlue
        "#FF8B0000", // DarkRed
        "#FF6B8E23", // OliveDrab
        "#FFD2691E", // Chocolate
        "#FF483D8B", // DarkSlateBlue
        "#FF20B2AA", // LightSeaGreen
        "#FFA0522D", // Sienna
        "#FF708090", // SlateGray
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
