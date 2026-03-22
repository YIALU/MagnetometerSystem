namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// Largest Triangle Three Buckets (LTTB) 降采样器
/// </summary>
public static class LttbDownsampler
{
    /// <summary>
    /// LTTB 降采样：将大量数据点降至 targetCount 个，保留波形特征。
    /// </summary>
    /// <param name="xs">时间轴数组（单调递增）</param>
    /// <param name="ys">值数组，与 xs 等长</param>
    /// <param name="targetCount">目标点数，必须 >= 2</param>
    /// <returns>降采样后的 (xs, ys) 元组；若原始点数 <= targetCount 则返回原数组的拷贝</returns>
    /// <exception cref="ArgumentException">xs 与 ys 长度不一致，或 targetCount &lt; 2</exception>
    public static (double[] xs, double[] ys) Downsample(double[] xs, double[] ys, int targetCount)
    {
        if (xs.Length != ys.Length)
            throw new ArgumentException("xs 与 ys 长度不一致");

        if (targetCount < 2)
            throw new ArgumentException("targetCount 必须 >= 2");

        int n = xs.Length;

        if (n == 0)
            return (Array.Empty<double>(), Array.Empty<double>());

        if (n <= targetCount)
        {
            return ((double[])xs.Clone(), (double[])ys.Clone());
        }

        var resultXs = new double[targetCount];
        var resultYs = new double[targetCount];

        // 第一个点始终保留
        resultXs[0] = xs[0];
        resultYs[0] = ys[0];

        double bucketSize = (double)(n - 2) / (targetCount - 2);

        int selectedIndex = 0; // 上一个已选点的索引

        for (int bucket = 1; bucket <= targetCount - 2; bucket++)
        {
            // 当前桶的范围
            int bucketStart = (int)Math.Floor(1 + (bucket - 1) * bucketSize);
            int bucketEnd = (int)Math.Floor(1 + bucket * bucketSize);
            if (bucketEnd > n - 1) bucketEnd = n - 1;

            // 下一个桶的平均点（用于三角形面积计算）
            int nextBucketStart = (int)Math.Floor(1 + bucket * bucketSize);
            int nextBucketEnd = (int)Math.Floor(1 + (bucket + 1) * bucketSize);
            if (nextBucketEnd > n - 1) nextBucketEnd = n - 1;

            double avgX = 0;
            double avgY = 0;
            int nextBucketCount = nextBucketEnd - nextBucketStart + 1;
            for (int i = nextBucketStart; i <= nextBucketEnd; i++)
            {
                avgX += xs[i];
                avgY += ys[i];
            }
            avgX /= nextBucketCount;
            avgY /= nextBucketCount;

            // 在当前桶中选择使三角形面积最大的点
            double maxArea = -1;
            int bestIndex = bucketStart;
            for (int i = bucketStart; i <= bucketEnd; i++)
            {
                double area = Math.Abs(
                    (xs[selectedIndex] - avgX) * (ys[i] - ys[selectedIndex])
                    - (xs[selectedIndex] - xs[i]) * (avgY - ys[selectedIndex])) * 0.5;
                if (area > maxArea)
                {
                    maxArea = area;
                    bestIndex = i;
                }
            }

            resultXs[bucket] = xs[bestIndex];
            resultYs[bucket] = ys[bestIndex];
            selectedIndex = bestIndex;
        }

        // 最后一个点始终保留
        resultXs[targetCount - 1] = xs[n - 1];
        resultYs[targetCount - 1] = ys[n - 1];

        return (resultXs, resultYs);
    }
}
