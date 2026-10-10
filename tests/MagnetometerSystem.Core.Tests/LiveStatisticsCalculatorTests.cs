using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class LiveStatisticsCalculatorTests
{
    private static readonly double[] Times = [0, 1, 2, 3, 4];

    [Fact]
    public void WithoutAStatisticsWindowTheDisplayWindowIsUsed()
    {
        var rows = LiveStatisticsCalculator.Compute(Times, [[1, 2, 3, 4, 5]], 1, ["Bx"], startIdx: 2, count: 3, windowSeconds: 0);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.ChannelIndex);
        Assert.Equal("Bx", row.Stats.ChannelName);
        Assert.Equal(3, row.Count);
        Assert.Equal(4, row.Stats.Mean, 12);
    }

    [Fact]
    public void AStatisticsWindowTakesTheLastSecondsRegardlessOfTheDisplayWindow()
    {
        var rows = LiveStatisticsCalculator.Compute(Times, [[1, 2, 3, 4, 5]], 1, ["Bx"], startIdx: 0, count: 5, windowSeconds: 1.5);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Count);            // t = 3、4
        Assert.Equal(4.5, row.Stats.Mean, 12);
    }

    [Fact]
    public void NonFiniteValuesAreIgnoredAndChannelsWithoutDataAreLeftOut()
    {
        double[][] channels = [[1, double.NaN, 3, double.PositiveInfinity, 5], [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN], [1, 2], [7, 7, 7, 7, 7]];

        var rows = LiveStatisticsCalculator.Compute(Times, channels, channelCount: 3, [], startIdx: 0, count: 5, windowSeconds: 0);

        // 第 2 路全为 NaN，第 3 路长度不够，第 4 路超出通道数。
        var row = Assert.Single(rows);
        Assert.Equal("CH0", row.Stats.ChannelName);
        Assert.Equal(3, row.Count);
        Assert.Equal(3, row.Stats.Mean, 12);
    }
}
