using MagnetometerSystem.Core.Helpers;
using Xunit;

namespace MagnetometerSystem.Core.Tests;

public class MinMaxDecimatorTests
{
    [Fact]
    public void LargeInputKeepsExtremesSkipsNonFiniteAndStaysBounded()
    {
        const int n = 1_000_000;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = i * 0.001;
            ys[i] = i % 97 == 0 ? double.NaN : Math.Sin(i * 0.0005);
        }
        ys[123_457] = 50;   // 全局最大
        ys[876_543] = -50;  // 全局最小

        var (dx, dy) = MinMaxDecimator.Decimate(xs, ys, 4000);

        Assert.InRange(dx.Length, 2, 4000);
        Assert.Equal(dx.Length, dy.Length);
        Assert.All(dy, y => Assert.True(double.IsFinite(y)));
        Assert.Contains(50, dy);
        Assert.Contains(-50, dy);
        Assert.True(dx.Zip(dx.Skip(1)).All(p => p.Second > p.First));
    }

    [Fact]
    public void SmallInputReturnsAllFinitePointsInOrder()
    {
        var (dx, dy) = MinMaxDecimator.Decimate([0, 1, 2, 3], [5, double.NaN, 7, 8], 4000);
        Assert.Equal(new double[] { 0, 2, 3 }, dx);
        Assert.Equal(new double[] { 5, 7, 8 }, dy);
    }
}
