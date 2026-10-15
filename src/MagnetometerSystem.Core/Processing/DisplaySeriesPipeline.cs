using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Processing;

/// <summary>图表显示滤波设置；未启用或窗口不大于 1 时不滤波。</summary>
public readonly record struct DisplayFilterSettings(bool Enabled, FilterType Type, int WindowSize);

/// <summary>
/// 一条曲线的显示数据：降采样后的坐标，以及加偏移、滤波之后、降采样之前的数值（多图右上角的统计标注用）。
/// </summary>
public readonly record struct DisplaySeries(double[] Xs, double[] Ys, double[] Values);

/// <summary>
/// 曲线显示流水线：加显示偏移 → 滤波 → 降采样。只处理显示副本，不改变保存的原始值、统计表和十字准线读数。
/// </summary>
public sealed class DisplaySeriesPipeline
{
    private readonly DataProcessor _dataProcessor = new();

    /// <summary>
    /// 完整流水线。<paramref name="values"/> 必须是窗口数据的副本：偏移非 0 时直接加在它上面。
    /// </summary>
    public DisplaySeries Prepare(double[] times, double[] values, double offset,
        DisplayFilterSettings filter, int downsampleTargetCount)
    {
        if (offset != 0)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] += offset;
        }

        var filtered = Filter(values, filter);
        var (xs, ys) = Downsample(times, filtered, downsampleTargetCount);
        return new DisplaySeries(xs, ys, filtered);
    }

    /// <summary>按滤波设置处理数据；不滤波时原样返回同一个数组。</summary>
    public double[] Filter(double[] values, DisplayFilterSettings filter)
    {
        if (!filter.Enabled || filter.WindowSize <= 1 || values.Length == 0)
            return values;

        return filter.Type switch
        {
            FilterType.MovingAverage => _dataProcessor.MovingAverage(values, filter.WindowSize),
            FilterType.Median => _dataProcessor.MedianFilter(values, filter.WindowSize),
            _ => values
        };
    }

    /// <summary>LTTB 降采样到目标点数；目标不大于 0 或点数不超过目标时原样返回。</summary>
    public static (double[] Xs, double[] Ys) Downsample(double[] times, double[] values, int targetCount)
    {
        if (targetCount <= 0 || times.Length <= targetCount)
            return (times, values);

        return LttbDownsampler.Downsample(times, values, targetCount);
    }
}
