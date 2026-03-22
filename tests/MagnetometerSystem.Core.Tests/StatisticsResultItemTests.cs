using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class StatisticsResultItemTests
{
    [Fact]
    public void Compute_CalculatesCorrectStatistics()
    {
        var data = new double[] { 10, 20, 30, 40, 50 };

        var result = StatisticsResultItem.Compute("Test", data);

        Assert.Equal("Test", result.ChannelName);
        Assert.Equal(30.0, result.Mean, precision: 6);
        Assert.Equal(10.0, result.Min, precision: 6);
        Assert.Equal(50.0, result.Max, precision: 6);
        Assert.Equal(40.0, result.PeakToPeak, precision: 6); // 50-10
        Assert.True(result.StdDev > 0);
        Assert.True(result.Rms > 0);
    }

    [Fact]
    public void Compute_SingleValue_ZeroStdDev()
    {
        var data = new double[] { 42.0 };

        var result = StatisticsResultItem.Compute("Ch1", data);

        Assert.Equal(42.0, result.Mean, precision: 6);
        Assert.Equal(0.0, result.StdDev, precision: 6);
        Assert.Equal(0.0, result.PeakToPeak, precision: 6);
    }

    [Fact]
    public void Compute_EmptyData_ReturnsDefault()
    {
        var result = StatisticsResultItem.Compute("Empty", ReadOnlySpan<double>.Empty);

        Assert.Equal("Empty", result.ChannelName);
        Assert.Equal(0.0, result.Mean);
    }

    [Fact]
    public void Format_RespectsConfig()
    {
        var item = new StatisticsResultItem
        {
            ChannelName = "Bx",
            Mean = 50000,
            StdDev = 5.5,
            PeakToPeak = 22,
            Rms = 50000.1,
            Min = 49989,
            Max = 50011,
        };

        var config = new StatisticsConfig
        {
            ShowMean = true,
            ShowStdDev = true,
            ShowPeakToPeak = false,
            ShowRms = false,
            ShowMin = false,
            ShowMax = false,
        };

        string formatted = item.Format(config);

        Assert.Contains("Avg:", formatted);
        Assert.Contains("Std:", formatted);
        Assert.DoesNotContain("PP:", formatted);
        Assert.DoesNotContain("RMS:", formatted);
    }
}
