namespace MagnetometerSystem.Core.Models;

/// <summary>
/// Statistics computed for a selected data interval
/// </summary>
public class IntervalStatisticsResult
{
    public IntervalSelection Interval { get; init; } = null!;
    public int SampleCount { get; init; }
    public IReadOnlyList<StatisticsResultItem> ChannelStats { get; init; } = [];

    public static IntervalStatisticsResult Compute(
        IntervalSelection interval,
        double[] times,
        double[][] channelData,
        string[] channelNames)
    {
        var (startIdx, count) = interval.GetIndices(times);

        if (count == 0)
        {
            return new IntervalStatisticsResult
            {
                Interval = interval,
                SampleCount = 0,
                ChannelStats = []
            };
        }

        var stats = new List<StatisticsResultItem>();
        for (int ch = 0; ch < channelData.Length && ch < channelNames.Length; ch++)
        {
            var span = channelData[ch].AsSpan(startIdx, count);
            stats.Add(StatisticsResultItem.Compute(channelNames[ch], span));
        }

        return new IntervalStatisticsResult
        {
            Interval = interval,
            SampleCount = count,
            ChannelStats = stats
        };
    }
}
