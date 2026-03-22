namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 数据处理器实现，提供移动平均和中值滤波算法。
/// </summary>
public class DataProcessor : IDataProcessor
{
    /// <inheritdoc />
    public double[] MovingAverage(double[] data, int windowSize)
    {
        if (data is null || data.Length == 0)
            return Array.Empty<double>();

        if (windowSize < 1)
            windowSize = 1;

        int n = data.Length;
        int halfW = windowSize / 2;
        var result = new double[n];

        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - halfW);
            int end = Math.Min(n - 1, i + halfW);
            double sum = 0;
            for (int j = start; j <= end; j++)
                sum += data[j];
            result[i] = sum / (end - start + 1);
        }

        return result;
    }

    /// <inheritdoc />
    public double[] MedianFilter(double[] data, int windowSize)
    {
        if (data is null || data.Length == 0)
            return Array.Empty<double>();

        if (windowSize < 1)
            windowSize = 1;

        // 确保窗口大小为奇数
        if (windowSize % 2 == 0)
            windowSize++;

        int n = data.Length;
        int halfW = windowSize / 2;
        var result = new double[n];

        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - halfW);
            int end = Math.Min(n - 1, i + halfW);
            int windowLength = end - start + 1;

            // 复制窗口数据并排序
            var window = new double[windowLength];
            data.AsSpan(start, windowLength).CopyTo(window);
            window.AsSpan().Sort();

            result[i] = window[windowLength / 2];
        }

        return result;
    }
}
