namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 自定义滚动统计配置
/// </summary>
public class StatisticsConfig
{
    /// <summary>统计时间窗口（秒），0 表示使用图表时间窗口</summary>
    public double WindowSeconds { get; set; } = 60;

    /// <summary>是否显示均值</summary>
    public bool ShowMean { get; set; } = true;

    /// <summary>是否显示标准差</summary>
    public bool ShowStdDev { get; set; } = true;

    /// <summary>是否显示峰峰值 (max - min)</summary>
    public bool ShowPeakToPeak { get; set; } = true;

    /// <summary>是否显示 RMS（均方根）</summary>
    public bool ShowRms { get; set; }

    /// <summary>是否显示最小值</summary>
    public bool ShowMin { get; set; }

    /// <summary>是否显示最大值</summary>
    public bool ShowMax { get; set; }
}
