using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class RollingStatisticsEngineTests
{
    [Fact]
    public void AddSample_And_ComputeAll_BasicStatistics()
    {
        var engine = new RollingStatisticsEngine(3);
        engine.Configure(new StatisticsConfig { WindowSeconds = 100 });

        engine.AddSample(0, new[] { 10.0, 20.0, 30.0 });
        engine.AddSample(1, new[] { 20.0, 20.0, 30.0 });
        engine.AddSample(2, new[] { 30.0, 20.0, 30.0 });

        var results = engine.ComputeAll(new[] { "Ch1", "Ch2", "Ch3" });

        Assert.Equal(3, results.Length);

        // Ch1: [10, 20, 30] → mean=20
        Assert.Equal(20.0, results[0].Mean, precision: 2);
        Assert.Equal(20.0, results[0].PeakToPeak, precision: 2); // 30-10
        Assert.Equal(10.0, results[0].Min, precision: 2);
        Assert.Equal(30.0, results[0].Max, precision: 2);

        // Ch2: [20, 20, 20] → mean=20, stddev=0
        Assert.Equal(20.0, results[1].Mean, precision: 2);
        Assert.Equal(0.0, results[1].StdDev, precision: 2);
    }

    [Fact]
    public void ComputeAll_RespectsWindowSeconds()
    {
        var engine = new RollingStatisticsEngine(1);
        engine.Configure(new StatisticsConfig { WindowSeconds = 5 });

        // 添加窗口外的旧数据
        engine.AddSample(0, new[] { 100.0 });
        engine.AddSample(1, new[] { 100.0 });
        // 窗口内的新数据
        engine.AddSample(10, new[] { 50.0 });
        engine.AddSample(11, new[] { 50.0 });
        engine.AddSample(12, new[] { 50.0 });

        var results = engine.ComputeAll(new[] { "Ch1" });

        // 窗口=5秒，最新时间戳=12，所以只包含 [7..12] 的数据
        Assert.Single(results);
        Assert.Equal(50.0, results[0].Mean, precision: 2);
    }

    [Fact]
    public void Clear_RemovesAllData()
    {
        var engine = new RollingStatisticsEngine(2);
        engine.AddSample(0, new[] { 10.0, 20.0 });
        engine.AddSample(1, new[] { 30.0, 40.0 });

        engine.Clear();

        var results = engine.ComputeAll(new[] { "Ch1", "Ch2" });
        Assert.Empty(results);
    }

    [Fact]
    public void AddSample_ThrowsOnExceedingMaxChannels()
    {
        var engine = new RollingStatisticsEngine(2);

        Assert.Throws<ArgumentException>(() =>
            engine.AddSample(0, new[] { 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidMaxChannels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingStatisticsEngine(0));
    }
}
