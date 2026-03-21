namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 图表显示设置
/// </summary>
public class ChartSettings
{
    /// <summary>Y 轴是否自动缩放</summary>
    public bool AutoScaleY { get; set; } = true;

    /// <summary>Y 轴最小值（手动模式）</summary>
    public double YMin { get; set; } = -100000;

    /// <summary>Y 轴最大值（手动模式）</summary>
    public double YMax { get; set; } = 100000;

    /// <summary>X 轴时间窗口（秒），0 表示显示全部</summary>
    public double TimeWindowSeconds { get; set; } = 30;

    /// <summary>图表刷新率 (fps)</summary>
    public int RefreshRate { get; set; } = 30;

    /// <summary>是否显示网格线</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>是否自动滚动到最新数据</summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>各通道是否可见（索引对应通道号）</summary>
    public bool[] ChannelVisible { get; set; } = [true, true, true, true, true, true, true, true];

    /// <summary>总场曲线是否可见</summary>
    public bool TotalFieldVisible { get; set; } = true;
}
