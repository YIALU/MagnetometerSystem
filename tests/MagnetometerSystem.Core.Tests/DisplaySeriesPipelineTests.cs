using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class DisplaySeriesPipelineTests
{
    private static readonly DisplayFilterSettings NoFilter = new(false, FilterType.MovingAverage, 5);

    [Fact]
    public void OffsetIsAddedBeforeFilteringAndValuesKeepTheFilteredCurve()
    {
        var pipeline = new DisplaySeriesPipeline();
        double[] times = [0, 1, 2, 3, 4];

        var series = pipeline.Prepare(times, [1, 2, 3, 4, 5], offset: 10,
            new DisplayFilterSettings(true, FilterType.MovingAverage, 3), downsampleTargetCount: 0);

        // 窗口 3 的移动平均，两端按实际点数平均。
        Assert.Equal([11.5, 12, 13, 14, 14.5], series.Values);
        Assert.Same(times, series.Xs);
        Assert.Same(series.Values, series.Ys);
    }

    [Fact]
    public void ZeroOffsetWithoutFilterLeavesTheCopyUntouched()
    {
        var pipeline = new DisplaySeriesPipeline();
        double[] values = [-0.0, 2];

        var series = pipeline.Prepare([0, 1], values, offset: 0, NoFilter, downsampleTargetCount: 0);

        Assert.Same(values, series.Values);
        Assert.True(double.IsNegative(series.Values[0]));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    public void FilterReturnsTheSameArrayWhenDisabledOrTheWindowIsTooSmall(bool enabled, int window)
    {
        double[] values = [1, 100, 3];

        Assert.Same(values, new DisplaySeriesPipeline().Filter(values, new DisplayFilterSettings(enabled, FilterType.Median, window)));
    }

    [Fact]
    public void MedianFilterIsUsedWhenSelected()
    {
        var filtered = new DisplaySeriesPipeline().Filter([5, 1, 100, 3, 7], new DisplayFilterSettings(true, FilterType.Median, 3));

        Assert.Equal(5, filtered[1]);
        Assert.Equal(3, filtered[2]);
        Assert.Equal(7, filtered[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(11)]
    public void DownsampleKeepsTheInputWhenTheTargetIsOffOrNotBelowTheCount(int target)
    {
        var times = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        var values = times.Select(t => t * t).ToArray();

        var (xs, ys) = DisplaySeriesPipeline.Downsample(times, values, target);

        Assert.Same(times, xs);
        Assert.Same(values, ys);
    }

    [Fact]
    public void DownsampleReducesToTheTargetAndKeepsBothEnds()
    {
        var times = Enumerable.Range(0, 1000).Select(i => i * 0.01).ToArray();
        var values = times.Select(Math.Sin).ToArray();

        var series = new DisplaySeriesPipeline().Prepare(times, values, 0, NoFilter, downsampleTargetCount: 100);

        Assert.Equal(100, series.Xs.Length);
        Assert.Equal(100, series.Ys.Length);
        Assert.Equal(times[0], series.Xs[0]);
        Assert.Equal(times[^1], series.Xs[^1]);
        Assert.Equal(1000, series.Values.Length);   // 统计标注用降采样前的数据
    }
}
