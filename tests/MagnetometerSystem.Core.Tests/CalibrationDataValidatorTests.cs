using MagnetometerSystem.Core.Calibration;

namespace MagnetometerSystem.Core.Tests;

public class CalibrationDataValidatorTests
{
    /// <summary>生成球面均匀分布的三轴数据</summary>
    private static List<double[]> GenerateSphericalData(int count, double totalField = 50000)
    {
        var data = new List<double[]>();
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            // 均匀球面采样
            double theta = Math.Acos(2 * rng.NextDouble() - 1); // [0, PI]
            double phi = 2 * Math.PI * rng.NextDouble();         // [0, 2PI]
            double bx = totalField * Math.Sin(theta) * Math.Cos(phi);
            double by = totalField * Math.Sin(theta) * Math.Sin(phi);
            double bz = totalField * Math.Cos(theta);
            data.Add(new[] { bx, by, bz });
        }
        return data;
    }

    [Fact]
    public void Validate_EmptyData_IsInvalid()
    {
        var result = CalibrationDataValidator.Validate(new List<double[]>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("无数据"));
    }

    [Fact]
    public void Validate_GoodData_NoWarnings()
    {
        var data = GenerateSphericalData(500, 50000);

        var result = CalibrationDataValidator.Validate(data);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.Equal(500, result.SampleCount);
        Assert.InRange(result.MeanTotalField, 45000, 55000);
    }

    [Fact]
    public void Validate_FewSamples_WarnsButStillValid()
    {
        var data = GenerateSphericalData(50, 50000);

        var result = CalibrationDataValidator.Validate(data);

        Assert.True(result.IsValid); // 仍然有效
        Assert.Contains(result.Warnings, w => w.Contains("样本数量较少"));
    }

    [Fact]
    public void Validate_AbnormalFieldStrength_WarnsButStillValid()
    {
        // 总场 100000 nT，超出典型范围
        var data = GenerateSphericalData(300, 100000);

        var result = CalibrationDataValidator.Validate(data);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("超出典型地磁场范围"));
    }

    [Fact]
    public void RemoveOutliers_RemovesAnomalousPoints()
    {
        var data = GenerateSphericalData(200, 50000);
        // 插入几个明显异常的点
        data.Add(new[] { 200000.0, 0.0, 0.0 });
        data.Add(new[] { 0.0, 200000.0, 0.0 });

        var filtered = CalibrationDataValidator.RemoveOutliers(data);

        Assert.True(filtered.Count < data.Count);
        Assert.True(filtered.Count >= 200); // 正常数据应保留
    }

    [Fact]
    public void RemoveOutliers_SmallDataset_ReturnsAsIs()
    {
        var data = new List<double[]>
        {
            new[] { 1.0, 2.0, 3.0 },
            new[] { 4.0, 5.0, 6.0 },
        };

        var filtered = CalibrationDataValidator.RemoveOutliers(data);

        Assert.Equal(data.Count, filtered.Count);
    }

    [Fact]
    public void Validate_SphericityCoverage_Calculated()
    {
        var data = GenerateSphericalData(500);

        var result = CalibrationDataValidator.Validate(data);

        Assert.True(result.SphericityCoverage > 0);
        Assert.True(result.SphericityCoverage <= 1.0);
    }
}
