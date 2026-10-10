using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>一个通道在统计窗口内的原始值统计，以及参与统计的有限值个数。</summary>
public sealed record ChannelWindowStatistics(int ChannelIndex, StatisticsResultItem Stats, int Count);

/// <summary>实时图表的滚动统计：按统计窗口对每个通道的原始值求统计量。刷新节流由调用方负责。</summary>
public static class LiveStatisticsCalculator
{
    /// <summary>
    /// <paramref name="windowSeconds"/> 大于 0 时统计最后这么多秒的数据，否则统计显示窗口 [startIdx, startIdx + count)。
    /// 非有限值不参与统计；数据不足或窗口内没有有限值的通道不出现在结果里。名称缺失时用 CH{n}。
    /// </summary>
    public static List<ChannelWindowStatistics> Compute(double[] times, double[][] channelData, int channelCount,
        IReadOnlyList<string> channelNames, int startIdx, int count, double windowSeconds)
    {
        // 确定统计窗口
        int statStartIdx = startIdx;
        int statCount = count;

        if (windowSeconds > 0 && times.Length > 0)
        {
            double statXMin = times[^1] - windowSeconds;
            statStartIdx = 0;
            for (int i = times.Length - 1; i >= 0; i--)
            {
                if (times[i] < statXMin) { statStartIdx = i + 1; break; }
            }
            statStartIdx = Math.Min(statStartIdx, times.Length - 1);
            statCount = times.Length - statStartIdx;
        }

        var results = new List<ChannelWindowStatistics>();
        for (int ch = 0; ch < channelCount; ch++)
        {
            if (ch >= channelData.Length || channelData[ch].Length < statStartIdx + statCount)
                continue;

            var span = channelData[ch].AsSpan(statStartIdx, statCount).ToArray().Where(double.IsFinite).ToArray();
            if (span.Length == 0) continue;
            string name = ch < channelNames.Count ? channelNames[ch] : $"CH{ch}";
            results.Add(new ChannelWindowStatistics(ch, StatisticsResultItem.Compute(name, span), span.Length));
        }
        return results;
    }
}
