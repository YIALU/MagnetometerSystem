using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class GradientCalculatorTests
{
    [Fact]
    public void ComputeGradient_BasicCalculation()
    {
        double gradient = GradientCalculator.ComputeGradient(50100, 50000, 1.0);
        Assert.Equal(100.0, gradient);
    }

    [Fact]
    public void ComputeGradient_WithDistance()
    {
        double gradient = GradientCalculator.ComputeGradient(50200, 50000, 2.0);
        Assert.Equal(100.0, gradient);
    }

    [Fact]
    public void ComputeGradient_ThrowsOnZeroDistance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GradientCalculator.ComputeGradient(100, 200, 0));
    }

    [Fact]
    public void ComputeAxisGradients_BatchCalculation()
    {
        var s1 = new double[] { 50100, 50200, 50300 };
        var s2 = new double[] { 50000, 50000, 50000 };

        var result = GradientCalculator.ComputeAxisGradients(s1, s2, 1.0);

        Assert.Equal(3, result.Length);
        Assert.Equal(100.0, result[0]);
        Assert.Equal(200.0, result[1]);
        Assert.Equal(300.0, result[2]);
    }

    [Fact]
    public void ComputeAxisGradients_ThrowsOnMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            GradientCalculator.ComputeAxisGradients(new double[] { 1 }, new double[] { 1, 2 }, 1.0));
    }

    [Fact]
    public void ComputeAxisGradients_EmptyArrays_ReturnsEmpty()
    {
        var result = GradientCalculator.ComputeAxisGradients(
            Array.Empty<double>(), Array.Empty<double>(), 1.0);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeTotalFieldGradient_CalculatesCorrectly()
    {
        var s1 = new double[] { 30000, 10000, 40000 }; // |B| = 50990.2
        var s2 = new double[] { 30000, 10000, 39900 }; // |B| = 50923.5

        double gradient = GradientCalculator.ComputeTotalFieldGradient(s1, s2, 1.0);

        // 梯度应为正值（s1 总场 > s2 总场）
        Assert.True(gradient > 0);
        Assert.InRange(gradient, 50, 80);
    }
}
