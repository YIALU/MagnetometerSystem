namespace MagnetometerSystem.App.Helpers;

/// <summary>
/// 图表中文字体辅助类，统一应用 CJK 字体以确保中文标签正常显示
/// </summary>
public static class ChartFontHelper
{
    public const string DefaultCjkFont = "Microsoft YaHei UI";

    /// <summary>全局设置 ScottPlot 默认字体（在 App 启动时调用一次即可）</summary>
    public static void ApplyToAll()
    {
        ScottPlot.Fonts.Default = DefaultCjkFont;
    }

    /// <summary>对单个 Plot 实例应用 CJK 字体</summary>
    public static void Apply(ScottPlot.Plot plot)
    {
        plot.Axes.Title.Label.FontName = DefaultCjkFont;
        plot.Axes.Left.Label.FontName = DefaultCjkFont;
        plot.Axes.Bottom.Label.FontName = DefaultCjkFont;
        plot.Axes.Left.TickLabelStyle.FontName = DefaultCjkFont;
        plot.Axes.Bottom.TickLabelStyle.FontName = DefaultCjkFont;
        plot.Legend.FontName = DefaultCjkFont;
    }
}
