namespace MagnetometerSystem.Core.Calibration;

public class CalibrationDataValidation
{
    /// <summary>
    /// 所有检查项均为警告性质，不阻止后续操作。
    /// IsValid 仅在完全无数据时为 false。
    /// </summary>
    public bool IsValid { get; set; } = true;
    public List<string> Warnings { get; set; } = new();

    // 统计信息
    public double MeanTotalField { get; set; }
    public double TotalFieldStdDev { get; set; }
    public double TotalFieldCoeffOfVariation { get; set; }  // StdDev/Mean
    public int SampleCount { get; set; }
    public int RejectedCount { get; set; }  // 突变点数量
    public double SphericityCoverage { get; set; }  // 0~1
}

public static class CalibrationDataValidator
{
    // 地磁场合理范围 (nT)
    private const double MinFieldStrength = 20000;  // 留余量
    private const double MaxFieldStrength = 70000;

    // 总场变异系数阈值
    private const double MaxCoefficientOfVariation = 0.05;  // 5%

    // 相邻样本突变阈值
    private const double MaxJumpRatio = 0.10;  // 10%

    // 建议最小样本数
    private const int RecommendedMinSampleCount = 200;

    // 建议最小球面覆盖度
    private const double RecommendedMinCoverage = 0.3;  // 30%

    /// <summary>   
    /// 验证采集数据的合理性。
    /// 所有检查项均为警告，不阻止用户继续操作。
    /// 用户可能只需要部分旋转时段的数据，或从外部导入已筛选的数据。
    /// </summary>
    public static CalibrationDataValidation Validate(List<double[]> data)
    {
        var result = new CalibrationDataValidation();
        result.SampleCount = data.Count;

        if (data.Count == 0)
        {
            result.IsValid = false;
            result.Warnings.Add("无数据");
            return result;
        }

        // 1. 样本数量检查（仅警告）
        if (data.Count < RecommendedMinSampleCount)
        {
            result.Warnings.Add($"样本数量较少：当前 {data.Count} 个，建议至少 {RecommendedMinSampleCount} 个以获得更好的校正效果");
        }

        // 2. 计算总场
        var totalFields = new double[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            totalFields[i] = Math.Sqrt(data[i][0] * data[i][0] + data[i][1] * data[i][1] + data[i][2] * data[i][2]);
        }

        double mean = totalFields.Average();
        double stdDev = Math.Sqrt(totalFields.Select(v => (v - mean) * (v - mean)).Average());
        double cv = mean > 0 ? stdDev / mean : 0;

        result.MeanTotalField = mean;
        result.TotalFieldStdDev = stdDev;
        result.TotalFieldCoeffOfVariation = cv;

        // 3. 总场范围检查（仅警告）
        if (mean < MinFieldStrength || mean > MaxFieldStrength)
        {
            result.Warnings.Add($"平均总场 {mean:F0} nT 超出典型地磁场范围 ({MinFieldStrength}~{MaxFieldStrength} nT)，请确认传感器单位和环境");
        }

        // 4. 总场一致性检查（变异系数）
        if (cv > MaxCoefficientOfVariation)
        {
            result.Warnings.Add($"总场变异系数 {cv:P1} 偏大（建议 <{MaxCoefficientOfVariation:P0}），环境磁场可能不稳定");
        }

        // 5. 突变检测
        int jumpCount = 0;
        for (int i = 1; i < totalFields.Length; i++)
        {
            if (totalFields[i - 1] > 0)
            {
                double jump = Math.Abs(totalFields[i] - totalFields[i - 1]) / totalFields[i - 1];
                if (jump > MaxJumpRatio)
                    jumpCount++;
            }
        }
        result.RejectedCount = jumpCount;
        if (jumpCount > data.Count * 0.05)  // 超过5%的点有突变
        {
            result.Warnings.Add($"检测到 {jumpCount} 个突变点（相邻样本总场跳变 >{MaxJumpRatio:P0}），建议检查环境或筛选数据");
        }

        // 6. 球面覆盖度检查
        int latBins = 12, lonBins = 6;
        var covered = new bool[latBins, lonBins];
        foreach (var d in data)
        {
            double r = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (r < 1e-10) continue;
            double theta = Math.Acos(Math.Clamp(d[2] / r, -1, 1));  // 0~PI
            double phi = Math.Atan2(d[1], d[0]) + Math.PI;  // 0~2PI
            int latIdx = Math.Clamp((int)(theta / Math.PI * latBins), 0, latBins - 1);
            int lonIdx = Math.Clamp((int)(phi / (2 * Math.PI) * lonBins), 0, lonBins - 1);
            covered[latIdx, lonIdx] = true;
        }
        int totalBins = latBins * lonBins;
        int coveredCount = 0;
        for (int i = 0; i < latBins; i++)
            for (int j = 0; j < lonBins; j++)
                if (covered[i, j]) coveredCount++;
        result.SphericityCoverage = (double)coveredCount / totalBins;

        if (result.SphericityCoverage < RecommendedMinCoverage)
        {
            result.Warnings.Add($"球面覆盖度 {result.SphericityCoverage:P0} 较低（建议 ≥{RecommendedMinCoverage:P0}），旋转角度越大校正效果越好");
        }

        return result;
    }

    /// <summary>
    /// 剔除总场偏离均值超过 3 倍标准差的异常点
    /// </summary>
    public static List<double[]> RemoveOutliers(List<double[]> data)
    {
        if (data.Count < 10) return data;

        var totalFields = new double[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            totalFields[i] = Math.Sqrt(data[i][0] * data[i][0] + data[i][1] * data[i][1] + data[i][2] * data[i][2]);
        }

        double mean = totalFields.Average();
        double stdDev = Math.Sqrt(totalFields.Select(v => (v - mean) * (v - mean)).Average());
        double threshold = 3.0 * stdDev;

        var filtered = new List<double[]>();
        for (int i = 0; i < data.Count; i++)
        {
            if (Math.Abs(totalFields[i] - mean) <= threshold)
            {
                filtered.Add(data[i]);
            }
        }

        return filtered;
    }
}
