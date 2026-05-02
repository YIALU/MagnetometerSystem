namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 单通道统计结果
/// </summary>
public class StatisticsResultItem
{
    public string ChannelName { get; set; } = "";
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double PeakToPeak { get; set; }
    public double Rms { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }

    public string Format(StatisticsConfig config)
    {
        var parts = new List<string>();
        parts.Add(ChannelName);

        if (config.ShowMean) parts.Add($"Avg:{Mean:F2}");
        if (config.ShowStdDev) parts.Add($"Std:{StdDev:F2}");
        if (config.ShowPeakToPeak) parts.Add($"PP:{PeakToPeak:F2}");
        if (config.ShowRms) parts.Add($"RMS:{Rms:F2}");
        if (config.ShowMin) parts.Add($"Min:{Min:F2}");
        if (config.ShowMax) parts.Add($"Max:{Max:F2}");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 用于多图表右上角标注的多行统计摘要
    /// </summary>
    public string FormatMultiline()
    {
        return $"μ={Mean:F3}\nσ={StdDev:F3}\nmin={Min:F2}  max={Max:F2}\npp={PeakToPeak:F2}";
    }

    public static StatisticsResultItem Compute(string name, ReadOnlySpan<double> data)
    {
        if (data.Length == 0)
            return new StatisticsResultItem { ChannelName = name };

        double sum = 0, sumSq = 0, min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < data.Length; i++)
        {
            double v = data[i];
            sum += v;
            sumSq += v * v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double mean = sum / data.Length;
        double variance = sumSq / data.Length - mean * mean;
        if (variance < 0) variance = 0; // 数值精度保护

        return new StatisticsResultItem
        {
            ChannelName = name,
            Mean = mean,
            StdDev = Math.Sqrt(variance),
            PeakToPeak = max - min,
            Rms = Math.Sqrt(sumSq / data.Length),
            Min = min,
            Max = max,
        };
    }
}
