using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 要画的数据：缓冲中的全部时间点，以及按通道索引排列、与时间对齐的显示值；时间窗口是从 Start 起的 Count 个点。
/// </summary>
/// <param name="ChannelCount">通道布局的通道数；索引不小于它的通道不画。显示值可能少于这么多通道（晚到的通道），缺的通道没有曲线。</param>
public readonly record struct ChartWindowData(double[] Times, double[][] Channels, int Start, int Count, int ChannelCount);

/// <summary>曲线的显示设置：多图模式、显示滤波和降采样目标点数（不大于 0 时不降采样）。</summary>
public readonly record struct ChartDisplaySettings(bool MultiPlot, DisplayFilterSettings Filter, int DownsampleTargetCount);

/// <summary>
/// 把一个时间窗口的显示数据按通道配置算成 <see cref="ChartFrame"/>。协议通道按 <see cref="ChannelDisplayConfig.ChannelIndex"/>
/// 取数据，通道列表的顺序只决定多图的排列，不改变名称、单位与数值的对应。曲线经显示流水线（偏移 → 滤波 → 降采样）；
/// 计算通道某点缺数据时按 0 求值，公式无法解析时不画。只在界面线程使用。
/// </summary>
public sealed class ChartFrameBuilder(DisplaySeriesPipeline pipeline, ComputedChannelEvaluator evaluator)
{
    private const float ChannelLineWidth = 1.5f;

    public ChartFrame Build(ChartWindowData data, ChartAxes axes, ChartDisplaySettings display,
        IReadOnlyList<ChannelDisplayConfig> channels, IReadOnlyList<ComputedChannelDefinition> computed)
    {
        var windowTimes = data.Times.AsSpan(data.Start, data.Count).ToArray();
        return display.MultiPlot
            ? new ChartFrame(true, axes, [], [], Panels(data, windowTimes, display, channels, computed))
            : new ChartFrame(false, axes, UnitAxes(channels, computed), Curves(data, windowTimes, display, channels, computed), []);
    }

    /// <summary>单图的单位轴：可见通道与启用的计算通道的单位，去重，温度单位排在最后。</summary>
    private static string[] UnitAxes(IReadOnlyList<ChannelDisplayConfig> channels, IReadOnlyList<ComputedChannelDefinition> computed) =>
        channels.Where(c => c.Visible).Select(c => c.Unit)
            .Concat(computed.Where(c => c.Enabled).Select(c => c.Unit))
            .Distinct().OrderBy(u => u is "°C" or "℃" ? 1 : 0).ToArray();

    /// <summary>单图：协议通道按通道索引（还没有显示配置的通道用默认配色），然后是计算通道。</summary>
    private List<ChartCurve> Curves(ChartWindowData data, double[] windowTimes, ChartDisplaySettings display,
        IReadOnlyList<ChannelDisplayConfig> channels, IReadOnlyList<ComputedChannelDefinition> computed)
    {
        var curves = new List<ChartCurve>();
        for (int ch = 0; ch < data.ChannelCount; ch++)
        {
            var config = channels.FirstOrDefault(c => c.ChannelIndex == ch);
            if (config != null && !config.Visible)
                continue;

            if (!HasWindow(data, ch))
                continue;

            var series = Prepare(data, windowTimes, ch, config?.DisplayOffset ?? 0, display);
            curves.Add(new ChartCurve(series.Xs, series.Ys, config?.ParseColor(), ChannelLineWidth,
                (config?.Name ?? $"CH{ch}") + $" ({config?.Unit})", config?.Unit));
        }

        foreach (var (definition, series) in ComputedSeries(data, windowTimes, display, computed))
            curves.Add(new ChartCurve(series.Xs, series.Ys, ParseColor(definition.ColorHex), definition.LineWidth,
                definition.Name, definition.Unit));
        return curves;
    }

    /// <summary>多图：可见的协议通道按通道列表的顺序（用 ChannelIndex 取数据），然后是计算通道。</summary>
    private List<ChartPanel> Panels(ChartWindowData data, double[] windowTimes, ChartDisplaySettings display,
        IReadOnlyList<ChannelDisplayConfig> channels, IReadOnlyList<ComputedChannelDefinition> computed)
    {
        var panels = new List<ChartPanel>();
        foreach (var config in channels)
        {
            if (!config.Visible) continue;

            int ch = config.ChannelIndex;
            if (ch >= data.ChannelCount) break;

            ChartCurve? curve = null;
            string? statistics = null;
            if (HasWindow(data, ch))
            {
                var series = Prepare(data, windowTimes, ch, config.DisplayOffset, display);
                curve = new ChartCurve(series.Xs, series.Ys, config.ParseColor(), ChannelLineWidth, null, null);
                statistics = WindowStatistics(config.Name, series);
            }
            panels.Add(new ChartPanel($"{config.Name} ({config.Unit})", curve, statistics, config));
        }

        foreach (var (definition, series) in ComputedSeries(data, windowTimes, display, computed))
            panels.Add(new ChartPanel($"{definition.Name} ({definition.Unit})",
                new ChartCurve(series.Xs, series.Ys, ParseColor(definition.ColorHex), definition.LineWidth, null, null),
                WindowStatistics(definition.Name, series), null));
        return panels;
    }

    /// <summary>启用、有公式且公式能解析的计算通道，及其显示序列。</summary>
    private IEnumerable<(ComputedChannelDefinition Definition, DisplaySeries Series)> ComputedSeries(ChartWindowData data,
        double[] windowTimes, ChartDisplaySettings display, IReadOnlyList<ComputedChannelDefinition> computed)
    {
        foreach (var definition in computed)
        {
            if (!definition.Enabled || string.IsNullOrWhiteSpace(definition.Formula))
                continue;

            // 某通道缺这一点时按 0 求值
            var values = evaluator.Evaluate(definition.Formula, data.Channels, data.Start, data.Count, missing: 0);
            if (values == null) continue;

            yield return (definition, pipeline.Prepare(windowTimes, values, definition.DisplayOffset,
                display.Filter, display.DownsampleTargetCount));
        }
    }

    private static bool HasWindow(ChartWindowData data, int ch) =>
        ch < data.Channels.Length && data.Channels[ch].Length >= data.Start + data.Count;

    /// <summary>窗口数据的副本经显示流水线：流水线会把偏移直接加在传入的数组上。</summary>
    private DisplaySeries Prepare(ChartWindowData data, double[] windowTimes, int ch, double offset, ChartDisplaySettings display) =>
        pipeline.Prepare(windowTimes, data.Channels[ch].AsSpan(data.Start, data.Count).ToArray(), offset,
            display.Filter, display.DownsampleTargetCount);

    /// <summary>多图右上角的统计标注：显示窗口内、按显示值（偏移、滤波后，降采样前）计算。</summary>
    private static string WindowStatistics(string name, DisplaySeries series) =>
        "显示窗口\n" + StatisticsResultItem.Compute(name, series.Values).FormatMultiline();

    private static (byte A, byte R, byte G, byte B) ParseColor(string colorHex) =>
        new ChannelDisplayConfig { ColorHex = colorHex }.ParseColor();
}
