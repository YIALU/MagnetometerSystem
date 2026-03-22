namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 磁场梯度计算工具类。
/// </summary>
public static class GradientCalculator
{
    /// <summary>
    /// 计算两点间的磁场梯度。
    /// </summary>
    /// <param name="value1">传感器1的磁场值（nT）。</param>
    /// <param name="value2">传感器2的磁场值（nT）。</param>
    /// <param name="baselineDistanceMeters">基线距离（米），必须 &gt; 0。</param>
    /// <returns>梯度值（nT/m）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">baselineDistanceMeters &lt;= 0。</exception>
    public static double ComputeGradient(double value1, double value2, double baselineDistanceMeters)
    {
        if (baselineDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(baselineDistanceMeters), "基线距离必须大于零。");

        return (value1 - value2) / baselineDistanceMeters;
    }

    /// <summary>
    /// 批量计算两组传感器数据的逐点梯度。
    /// </summary>
    /// <param name="sensor1">传感器1的数据数组。</param>
    /// <param name="sensor2">传感器2的数据数组，长度必须与 sensor1 相同。</param>
    /// <param name="baselineDistanceMeters">基线距离（米），必须 &gt; 0。</param>
    /// <returns>梯度数组（nT/m），长度与输入相同。</returns>
    public static double[] ComputeAxisGradients(double[] sensor1, double[] sensor2, double baselineDistanceMeters)
    {
        ArgumentNullException.ThrowIfNull(sensor1);
        ArgumentNullException.ThrowIfNull(sensor2);

        if (baselineDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(baselineDistanceMeters), "基线距离必须大于零。");

        if (sensor1.Length != sensor2.Length)
            throw new ArgumentException("两组传感器数据长度必须相同。");

        if (sensor1.Length == 0)
            return Array.Empty<double>();

        var result = new double[sensor1.Length];
        for (int i = 0; i < sensor1.Length; i++)
            result[i] = (sensor1[i] - sensor2[i]) / baselineDistanceMeters;

        return result;
    }

    /// <summary>
    /// 计算总场梯度。
    /// </summary>
    /// <param name="sensor1">传感器1的各轴磁场值数组（nT）。</param>
    /// <param name="sensor2">传感器2的各轴磁场值数组（nT）。</param>
    /// <param name="baselineDistanceMeters">基线距离（米），必须 &gt; 0。</param>
    /// <returns>总场梯度值（nT/m）。</returns>
    public static double ComputeTotalFieldGradient(double[] sensor1, double[] sensor2, double baselineDistanceMeters)
    {
        ArgumentNullException.ThrowIfNull(sensor1);
        ArgumentNullException.ThrowIfNull(sensor2);

        if (baselineDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(baselineDistanceMeters), "基线距离必须大于零。");

        double sumSq1 = 0, sumSq2 = 0;
        for (int i = 0; i < sensor1.Length; i++)
            sumSq1 += sensor1[i] * sensor1[i];
        for (int i = 0; i < sensor2.Length; i++)
            sumSq2 += sensor2[i] * sensor2[i];

        return (Math.Sqrt(sumSq1) - Math.Sqrt(sumSq2)) / baselineDistanceMeters;
    }
}
