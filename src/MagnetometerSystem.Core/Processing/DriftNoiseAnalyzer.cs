namespace MagnetometerSystem.Core.Processing;

/// <summary>噪声与漂移分析的设置；所有时间均按记录时间戳计算，不使用标称采样率。</summary>
public sealed record DriftNoiseOptions
{
    /// <summary>短时噪声窗口长度（秒）。</summary>
    public double NoiseWindowSeconds { get; init; } = 10;

    /// <summary>每个噪声窗口内是否去除线性趋势；关闭时只去均值。</summary>
    public bool DetrendNoiseWindows { get; init; } = true;

    /// <summary>长时漂移分段长度（秒）。</summary>
    public double DriftSegmentSeconds { get; init; } = 60;

    /// <summary>相邻采样间隔超过“中位间隔 × 该倍数”记为缺失。</summary>
    public double GapFactor { get; init; } = 3;

    /// <summary>窗口或分段内至少需要的点数，不足的窗口不参与统计。</summary>
    public int MinPointsPerWindow { get; init; } = 3;
}

/// <summary>数据质量：点数、实际时长与间隔、缺失与非有限值。</summary>
public sealed record DataQuality(
    int SampleCount, int NonFiniteCount, double DurationSeconds,
    double MedianIntervalSeconds, double ActualRateHz, int GapCount, double MaxGapSeconds);

/// <summary>一个分段的均值，用于观察长时漂移。</summary>
public sealed record SegmentMean(double StartSeconds, double EndSeconds, int Count, double Mean);

/// <summary>一个噪声窗口（去趋势或去均值后）的标准差。</summary>
public sealed record NoiseWindow(double StartSeconds, int Count, double StdDev);

/// <summary>单通道的噪声与漂移分析结果，单位与输入通道相同（漂移速率为 单位/小时）。</summary>
public sealed record DriftNoiseResult(
    DataQuality Quality,
    double Mean, double StdDev, double Min, double Max, double PeakToPeak, double Rms,
    double DriftPerHour, double TrendIntercept, double TrendRSquared, double DetrendedStdDev,
    IReadOnlyList<SegmentMean> Segments, double SegmentMeanSpread,
    IReadOnlyList<NoiseWindow> NoiseWindows, double NoiseMedianStd, double NoiseMaxStd, double NoiseMedianPeakToPeak,
    IReadOnlyList<string> Warnings)
{
    public int NoiseWindowCount => NoiseWindows.Count;
}

/// <summary>
/// 短时噪声与长时漂移分析（REQ-005）。
/// <list type="bullet">
/// <item>漂移：对全部有效点按实际经过时间做最小二乘直线拟合，斜率换算为 每小时变化量；
/// 另按固定时长分段求均值，分段均值的极差反映慢变化幅度。</item>
/// <item>噪声：按固定时长切成不重叠窗口，窗口内去趋势（或仅去均值）后求标准差，
/// 取各窗口标准差的中位数作为噪声水平，最大值反映最差窗口。</item>
/// </list>
/// 非有限值（NaN/∞）在计算前剔除并计入数据质量。
/// </summary>
public static class DriftNoiseAnalyzer
{
    public static DriftNoiseResult Analyze(IReadOnlyList<DateTime> timestamps, IReadOnlyList<double> values, DriftNoiseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(values);
        if (timestamps.Count != values.Count)
            throw new ArgumentException("时间戳与数值数量不一致。");
        options ??= new DriftNoiseOptions();
        if (!(options.NoiseWindowSeconds > 0) || !(options.DriftSegmentSeconds > 0))
            throw new ArgumentException("噪声窗口和漂移分段长度必须为正数。");

        var warnings = new List<string>();
        var order = Enumerable.Range(0, timestamps.Count).OrderBy(i => timestamps[i]).ToArray();
        var t0 = order.Length > 0 ? timestamps[order[0]] : default;
        var ts = new List<double>(order.Length);
        var vs = new List<double>(order.Length);
        int nonFinite = 0;
        foreach (var i in order)
        {
            if (!double.IsFinite(values[i])) { nonFinite++; continue; }
            ts.Add((timestamps[i] - t0).TotalSeconds);
            vs.Add(values[i]);
        }

        var quality = ComputeQuality(ts, nonFinite, options.GapFactor);
        if (nonFinite > 0) warnings.Add($"已剔除 {nonFinite} 个非有限值。");
        if (quality.GapCount > 0) warnings.Add($"检测到 {quality.GapCount} 处采样缺失，最长 {quality.MaxGapSeconds:0.###} s。");

        if (vs.Count < 2)
        {
            warnings.Add("有效数据少于 2 点，无法计算趋势与噪声。");
            var single = vs.Count == 1 ? vs[0] : double.NaN;
            return new DriftNoiseResult(quality, single, double.NaN, single, single, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, [], double.NaN, [], double.NaN, double.NaN, double.NaN, warnings);
        }

        var (mean, std, min, max, rms) = Basic(vs, 0, vs.Count);
        var (slope, intercept, r2) = LinearFit(ts, vs, 0, vs.Count);
        var detrendedStd = ResidualStd(ts, vs, 0, vs.Count, slope, intercept);

        var segments = new List<SegmentMean>();
        foreach (var (start, count, segStart) in Windows(ts, options.DriftSegmentSeconds))
            if (count >= options.MinPointsPerWindow)
                segments.Add(new SegmentMean(segStart, segStart + options.DriftSegmentSeconds, count, Basic(vs, start, count).Mean));
        var spread = segments.Count > 0 ? segments.Max(s => s.Mean) - segments.Min(s => s.Mean) : double.NaN;
        if (segments.Count < 2) warnings.Add("数据时长不足两个漂移分段，分段均值仅供参考。");

        var windows = new List<NoiseWindow>();
        var windowPp = new List<double>();
        foreach (var (start, count, windowStart) in Windows(ts, options.NoiseWindowSeconds))
        {
            if (count < options.MinPointsPerWindow) continue;
            double s, k;
            if (options.DetrendNoiseWindows) (k, s, _) = LinearFit(ts, vs, start, count);
            else { k = 0; s = Basic(vs, start, count).Mean; }
            double sumSq = 0, lo = double.MaxValue, hi = double.MinValue;
            for (int i = start; i < start + count; i++)
            {
                var r = vs[i] - (s + k * ts[i]);
                sumSq += r * r;
                lo = Math.Min(lo, r); hi = Math.Max(hi, r);
            }
            windows.Add(new NoiseWindow(windowStart, count, Math.Sqrt(sumSq / count)));
            windowPp.Add(hi - lo);
        }
        var windowStds = windows.Select(w => w.StdDev).ToArray();
        if (windowStds.Length == 0) warnings.Add("没有满足最少点数的噪声窗口，请缩短窗口或选择更长的时间段。");

        return new DriftNoiseResult(quality, mean, std, min, max, max - min, rms,
            slope * 3600, intercept, r2, detrendedStd, segments, spread,
            windows, Median(windowStds), windowStds.Length > 0 ? windowStds.Max() : double.NaN, Median(windowPp),
            warnings);
    }

    private static DataQuality ComputeQuality(List<double> ts, int nonFinite, double gapFactor)
    {
        if (ts.Count < 2)
            return new DataQuality(ts.Count, nonFinite, 0, double.NaN, double.NaN, 0, 0);
        var intervals = new double[ts.Count - 1];
        for (int i = 1; i < ts.Count; i++) intervals[i - 1] = ts[i] - ts[i - 1];
        var median = Median(intervals);
        int gaps = 0; double maxGap = 0;
        if (median > 0)
            foreach (var dt in intervals)
                if (dt > median * gapFactor) { gaps++; maxGap = Math.Max(maxGap, dt); }
        var duration = ts[^1] - ts[0];
        return new DataQuality(ts.Count, nonFinite, duration, median,
            duration > 0 ? (ts.Count - 1) / duration : double.NaN, gaps, maxGap);
    }

    /// <summary>按实际时间切出不重叠窗口，返回 (起始下标, 点数, 窗口起始秒)。</summary>
    private static IEnumerable<(int Start, int Count, double WindowStart)> Windows(List<double> ts, double length)
    {
        int i = 0;
        while (i < ts.Count)
        {
            var windowStart = Math.Floor(ts[i] / length) * length;
            var end = windowStart + length;
            int j = i;
            while (j < ts.Count && ts[j] < end) j++;
            yield return (i, j - i, windowStart);
            i = j;
        }
    }

    private static (double Mean, double Std, double Min, double Max, double Rms) Basic(List<double> v, int start, int count)
    {
        double sum = 0, sumSq = 0, min = double.MaxValue, max = double.MinValue;
        for (int i = start; i < start + count; i++)
        {
            sum += v[i]; sumSq += v[i] * v[i];
            min = Math.Min(min, v[i]); max = Math.Max(max, v[i]);
        }
        var mean = sum / count;
        // 两遍法求方差，避免大均值（如 5 万 nT）下的精度损失。
        double dev = 0;
        for (int i = start; i < start + count; i++) dev += (v[i] - mean) * (v[i] - mean);
        return (mean, Math.Sqrt(dev / count), min, max, Math.Sqrt(sumSq / count));
    }

    /// <summary>最小二乘直线 v = intercept + slope·t；返回斜率、截距和决定系数。</summary>
    private static (double Slope, double Intercept, double RSquared) LinearFit(List<double> t, List<double> v, int start, int count)
    {
        double mt = 0, mv = 0;
        for (int i = start; i < start + count; i++) { mt += t[i]; mv += v[i]; }
        mt /= count; mv /= count;
        double stt = 0, stv = 0, svv = 0;
        for (int i = start; i < start + count; i++)
        {
            var dt = t[i] - mt; var dv = v[i] - mv;
            stt += dt * dt; stv += dt * dv; svv += dv * dv;
        }
        if (stt <= 0) return (0, mv, double.NaN);
        var slope = stv / stt;
        var r2 = svv > 0 ? stv * stv / (stt * svv) : 1;
        return (slope, mv - slope * mt, r2);
    }

    private static double ResidualStd(List<double> t, List<double> v, int start, int count, double slope, double intercept)
    {
        double sumSq = 0;
        for (int i = start; i < start + count; i++)
        {
            var r = v[i] - (intercept + slope * t[i]);
            sumSq += r * r;
        }
        return Math.Sqrt(sumSq / count);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(x => x).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
