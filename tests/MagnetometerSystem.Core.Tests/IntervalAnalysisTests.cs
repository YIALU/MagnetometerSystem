using MagnetometerSystem.Core.Models;
using Xunit;

namespace MagnetometerSystem.Core.Tests;

public class IntervalAnalysisTests
{
    [Fact]
    public void IntervalSelection_IsValid_WhenEndAfterStart()
    {
        var interval = new IntervalSelection(1.0, 5.0);
        Assert.True(interval.IsValid);
    }

    [Fact]
    public void IntervalSelection_IsInvalid_WhenEndBeforeStart()
    {
        var interval = new IntervalSelection(5.0, 1.0);
        Assert.False(interval.IsValid);
    }

    [Fact]
    public void IntervalSelection_IsInvalid_WhenZeroDuration()
    {
        var interval = new IntervalSelection(3.0, 3.0);
        Assert.False(interval.IsValid);
    }

    [Fact]
    public void IntervalSelection_Duration_ReturnsCorrectValue()
    {
        var interval = new IntervalSelection(2.0, 7.0);
        Assert.Equal(5.0, interval.Duration);
    }

    [Fact]
    public void IntervalSelection_GetIndices_ReturnsCorrectRange()
    {
        var times = new double[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0 };
        var interval = new IntervalSelection(2.0, 5.0);
        var (startIdx, count) = interval.GetIndices(times);
        Assert.Equal(2, startIdx);
        Assert.Equal(4, count); // indices 2,3,4,5
    }

    [Fact]
    public void IntervalSelection_GetIndices_EmptyArray_ReturnsZero()
    {
        var times = Array.Empty<double>();
        var interval = new IntervalSelection(1.0, 5.0);
        var (startIdx, count) = interval.GetIndices(times);
        Assert.Equal(0, startIdx);
        Assert.Equal(0, count);
    }

    [Fact]
    public void IntervalSelection_GetIndices_NoMatch_ReturnsZeroCount()
    {
        var times = new double[] { 0.0, 1.0, 2.0 };
        var interval = new IntervalSelection(5.0, 10.0);
        var (startIdx, count) = interval.GetIndices(times);
        Assert.Equal(0, count);
    }

    [Fact]
    public void IntervalSelection_GetIndices_PartialOverlap()
    {
        var times = new double[] { 0.0, 1.0, 2.0, 3.0, 4.0 };
        var interval = new IntervalSelection(2.5, 10.0);
        var (startIdx, count) = interval.GetIndices(times);
        Assert.Equal(3, startIdx); // first time >= 2.5 is index 3 (value 3.0)
        Assert.Equal(2, count); // indices 3,4
    }

    [Fact]
    public void IntervalStatisticsResult_ComputeFromData_CalculatesCorrectly()
    {
        var channelNames = new[] { "X", "Y" };
        var times = new double[] { 0, 1, 2, 3, 4 };
        var channelData = new double[][]
        {
            new double[] { 10, 20, 30, 40, 50 },
            new double[] { 100, 200, 300, 400, 500 }
        };
        var interval = new IntervalSelection(1.0, 3.0);

        var result = IntervalStatisticsResult.Compute(interval, times, channelData, channelNames);

        Assert.Equal(3, result.SampleCount);
        Assert.Equal(2, result.ChannelStats.Count);
        Assert.Equal(30.0, result.ChannelStats[0].Mean, 1);
        Assert.Equal("X", result.ChannelStats[0].ChannelName);
    }
}
