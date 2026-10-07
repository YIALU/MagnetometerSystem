using MagnetometerSystem.Core.Processing;
using Xunit;

namespace MagnetometerSystem.Core.Tests;

public class DriftNoiseAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Local);

    private static (DateTime[] T, double[] V) Series(int count, double dt, Func<int, double, double> value)
    {
        var t = new DateTime[count];
        var v = new double[count];
        for (int i = 0; i < count; i++)
        {
            var s = i * dt;
            t[i] = T0.AddSeconds(s);
            v[i] = value(i, s);
        }
        return (t, v);
    }

    [Fact]
    public void ConstantSignalHasNoDriftOrNoise()
    {
        var (t, v) = Series(600, 0.1, (_, _) => 50_000);
        var r = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 5, DriftSegmentSeconds = 20 });
        Assert.Equal(50_000, r.Mean, 9);
        Assert.Equal(0, r.StdDev, 9);
        Assert.Equal(0, r.DriftPerHour, 9);
        Assert.Equal(0, r.NoiseMedianStd, 9);
        Assert.Equal(0, r.SegmentMeanSpread, 9);
        Assert.Equal(600, r.Quality.SampleCount);
        Assert.Equal(10, r.Quality.ActualRateHz, 6);
        Assert.Equal(0, r.Quality.GapCount);
    }

    [Fact]
    public void KnownLinearDriftIsReportedPerHourAndRemovedFromNoise()
    {
        // 0.5 nT/min = 30 nT/h, 2 小时，1 Hz
        var (t, v) = Series(7200, 1, (_, s) => 48_000 + 0.5 / 60 * s);
        var r = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 60, DriftSegmentSeconds = 600 });
        Assert.Equal(30, r.DriftPerHour, 6);
        Assert.Equal(1, r.TrendRSquared, 9);
        Assert.Equal(0, r.DetrendedStdDev, 6);
        Assert.Equal(0, r.NoiseMedianStd, 6);
        Assert.Equal(12, r.Segments.Count);
        Assert.Equal(55, r.SegmentMeanSpread, 6); // 11 个分段间隔 × 5 nT
    }

    [Fact]
    public void MeanOnlyWindowsIncludeTrendWhileDetrendedWindowsDoNot()
    {
        var (t, v) = Series(1000, 0.1, (_, s) => s);
        var detrended = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 10 });
        var meanOnly = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 10, DetrendNoiseWindows = false });
        Assert.Equal(0, detrended.NoiseMedianStd, 6);
        Assert.True(meanOnly.NoiseMedianStd > 2.8); // 10 s 斜坡的标准差 ≈ 2.89
    }

    [Fact]
    public void SeededWhiteNoiseLevelMatchesGeneratedSigma()
    {
        var rng = new Random(20261006);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var (t, v) = Series(20_000, 0.02, (_, s) => 50_000 + 0.002 * s + 0.8 * Gauss());
        var r = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 5, DriftSegmentSeconds = 60 });
        Assert.InRange(r.NoiseMedianStd, 0.74, 0.86);
        Assert.InRange(r.DriftPerHour, 7.2 - 1, 7.2 + 1);
        Assert.InRange(r.DetrendedStdDev, 0.76, 0.84);
    }

    [Fact]
    public void UnevenTimestampsUseActualTimeAndCountGaps()
    {
        var times = new List<DateTime>();
        var values = new List<double>();
        for (int i = 0; i < 100; i++) { times.Add(T0.AddSeconds(i)); values.Add(2 * i); }
        // 10 s 缺失后继续，斜率仍为 2/s
        for (int i = 110; i < 200; i++) { times.Add(T0.AddSeconds(i)); values.Add(2 * i); }
        values[5] = double.NaN;
        var r = DriftNoiseAnalyzer.Analyze(times, values, new DriftNoiseOptions { NoiseWindowSeconds = 20, DriftSegmentSeconds = 50 });
        Assert.Equal(7200, r.DriftPerHour, 6);
        Assert.Equal(1, r.Quality.GapCount);
        Assert.Equal(11, r.Quality.MaxGapSeconds, 6);
        Assert.Equal(1, r.Quality.NonFiniteCount);
        Assert.Equal(189, r.Quality.SampleCount);
        Assert.Equal(199, r.Quality.DurationSeconds, 6);
        Assert.Contains(r.Warnings, w => w.Contains("缺失"));
    }

    [Fact]
    public void InsufficientDataReturnsExplanationInsteadOfThrowing()
    {
        var r = DriftNoiseAnalyzer.Analyze([T0], [1.0]);
        Assert.True(double.IsNaN(r.DriftPerHour));
        Assert.Equal(0, r.NoiseWindowCount);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            DriftNoiseAnalyzer.Analyze([T0, T0.AddSeconds(1)], [1.0, 2.0], new DriftNoiseOptions { NoiseWindowSeconds = 0 }));
        // 非有限长度会让窗口切分无法前进，必须在开始前拒绝。
        foreach (var bad in new[] { double.PositiveInfinity, double.NaN })
        {
            Assert.Throws<ArgumentException>(() =>
                DriftNoiseAnalyzer.Analyze([T0, T0.AddSeconds(1)], [1.0, 2.0], new DriftNoiseOptions { NoiseWindowSeconds = bad }));
            Assert.Throws<ArgumentException>(() =>
                DriftNoiseAnalyzer.Analyze([T0, T0.AddSeconds(1)], [1.0, 2.0], new DriftNoiseOptions { DriftSegmentSeconds = bad }));
        }
    }

    [Fact]
    public void ManyShortWindowsKeepBoundedDisplayRecordsButExactStatistics()
    {
        // 1 kHz、200 秒；3.5 ms 窗口（每窗 3 或 4 点）约 5.7 万个、1 s 分段 200 个：显示记录有上限，计数与统计按全部窗口。
        var (t, v) = Series(200_000, 0.001, (i, _) => i % 3 == 0 ? 1 : -1);
        var r = DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 0.0035, DriftSegmentSeconds = 1 });
        Assert.True(r.NoiseWindowCount > DriftNoiseAnalyzer.MaxRetainedWindows);
        Assert.InRange(r.NoiseWindows.Count, 1, DriftNoiseAnalyzer.MaxRetainedWindows);
        Assert.Equal(0, r.NoiseWindows[0].StartSeconds, 9);
        Assert.True(r.NoiseWindows.Zip(r.NoiseWindows.Skip(1)).All(p => p.Second.StartSeconds > p.First.StartSeconds));
        Assert.Equal(200, r.SegmentCount);
        Assert.Equal(200, r.Segments.Count); // 未超过上限时全部保留
        // 计数是全部窗口（约 200 s / 3.5 ms），不是保留下来用于显示的条数。
        Assert.InRange(r.NoiseWindowCount, 56_500, 57_500);
        Assert.True(double.IsFinite(r.NoiseMedianStd) && r.NoiseMedianStd > 0);
    }

    [Fact]
    public void TinyFiniteWindowsStillTerminate()
    {
        var (t, v) = Series(200, 1, (_, s) => s);
        // 窗口相对时间戳极短时，窗口终点会被舍入回起点；切分仍须每次至少前进一个点。
        var run = Task.Run(() => DriftNoiseAnalyzer.Analyze(t, v, new DriftNoiseOptions { NoiseWindowSeconds = 1e-300, DriftSegmentSeconds = 1e-300 }));
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "窗口切分没有结束");
        Assert.Empty(run.Result.NoiseWindows); // 每个窗口只有 1 点，少于最少点数
    }

    [Fact]
    public void CancellationIsObservedDuringTheComputation()
    {
        using var cts = new CancellationTokenSource();
        var (t, v) = Series(300_000, 0.01, (_, s) => s);
        // 读取 1000 个数值后取消：分析在计算过程中就应停止，而不是算完整个通道。
        var values = new CancelAfterReads(v, 1000, cts);
        Assert.Throws<OperationCanceledException>(() => DriftNoiseAnalyzer.Analyze(t, values, null, cts.Token));
        Assert.True(values.Reads < v.Length, "取消后仍读完了全部数值");
    }

    private sealed class CancelAfterReads(double[] values, int limit, CancellationTokenSource cts) : IReadOnlyList<double>
    {
        public int Reads { get; private set; }
        public double this[int index]
        {
            get
            {
                if (++Reads == limit) cts.Cancel();
                return values[index];
            }
        }
        public int Count => values.Length;
        public IEnumerator<double> GetEnumerator() => ((IEnumerable<double>)values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
