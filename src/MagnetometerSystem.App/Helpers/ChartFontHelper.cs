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
        ApplyTheme(plot);
    }

    /// <summary>图表配色与界面令牌一致：浅色面板底、淡网格、墨色坐标轴。</summary>
    public static void ApplyTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#FBFCFB");
        plot.DataBackground.Color = ScottPlot.Colors.White;
        plot.Axes.Color(ScottPlot.Color.FromHex("#66726E"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E4E9E6");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#FBFCFB").WithAlpha(.92);
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#D2D9D5");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#1C2422");
        plot.Legend.FontSize = 12;
        plot.Legend.Alignment = ScottPlot.Alignment.UpperLeft;
        plot.Axes.Left.Label.FontSize = 12;
        plot.Axes.Bottom.Label.FontSize = 12;
        plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#4B5753");
        plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#4B5753");
    }
}
