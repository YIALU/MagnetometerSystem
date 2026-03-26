using System.ComponentModel;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 单通道的显示配置（偏移、颜色、可见性等）
/// </summary>
public class ChannelDisplayConfig : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>通道名称</summary>
    public string Name { get; set; } = "CH0";

    /// <summary>通道索引</summary>
    public int ChannelIndex { get; set; }

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
    public bool Visible { get; set; } = true;

    /// <summary>曲线颜色（ARGB hex 字符串，如 "#FF0000FF"）</summary>
    public string ColorHex { get; set; } = "#FF0000FF";

    /// <summary>预设颜色列表</summary>
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
