using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Tests;

public class LttbDownsamplerTests
{
    [Fact]
    public void Downsample_PreservesFirstAndLastPoints()
    {
        var xs = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        var ys = xs.Select(x => Math.Sin(x * 0.1)).ToArray();

        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 10);

        Assert.Equal(10, rx.Length);
        Assert.Equal(xs[0], rx[0]);
        Assert.Equal(xs[99], rx[9]);
        Assert.Equal(ys[0], ry[0]);
        Assert.Equal(ys[99], ry[9]);
    }

    [Fact]
    public void Downsample_ReturnsClone_WhenDataSmallerThanTarget()
    {
        var xs = new double[] { 1, 2, 3 };
        var ys = new double[] { 10, 20, 30 };

        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 10);

        Assert.Equal(3, rx.Length);
        Assert.Equal(xs, rx);
        // 应该是副本，不是同一个引用
        Assert.NotSame(xs, rx);
    }

    [Fact]
    public void Downsample_EmptyInput_ReturnsEmpty()
    {
        var (rx, ry) = LttbDownsampler.Downsample(
            Array.Empty<double>(), Array.Empty<double>(), 5);

        Assert.Empty(rx);
        Assert.Empty(ry);
    }

    [Fact]
    public void Downsample_ThrowsOnMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            LttbDownsampler.Downsample(new double[] { 1, 2 }, new double[] { 1 }, 2));
    }

    [Fact]
    public void Downsample_ThrowsOnTargetCountLessThan2()
    {
        Assert.Throws<ArgumentException>(() =>
            LttbDownsampler.Downsample(new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 }, 1));
    }

    [Fact]
    public void Downsample_OutputCountMatchesTarget()
    {
        var xs = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();
        var ys = xs.Select(x => Math.Sin(x * 0.01) * 50000).ToArray();

        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 50);

        Assert.Equal(50, rx.Length);
        Assert.Equal(50, ry.Length);
    }
}
