using MagnetometerSystem.Core.Calibration;

namespace MagnetometerSystem.Core.Tests;

public class SphericalCoverageEstimatorTests
{
    // 12 经度 × 6 纬度，每格 30°。
    private static readonly double[] Center = [1000, -2000, 500];

    /// <summary>以 Center 为球心、方向落在 (经度格, 纬度格) 中心的样本。</summary>
    private static double[] AtCell(int lonIdx, int latIdx, double radius = 50)
    {
        double lon = (-180 + 30 * lonIdx + 15) * Math.PI / 180;
        double lat = (-90 + 30 * latIdx + 15) * Math.PI / 180;
        return
        [
            Center[0] + radius * Math.Cos(lat) * Math.Cos(lon),
            Center[1] + radius * Math.Cos(lat) * Math.Sin(lon),
            Center[2] + radius * Math.Sin(lat),
        ];
    }

    [Fact]
    public void NoSamplesMeansNoCoverage() =>
        Assert.Equal(0, SphericalCoverageEstimator.Estimate([]));

    [Fact]
    public void SamplesAtTheCenterDoNotCount() =>
        Assert.Equal(0, SphericalCoverageEstimator.Estimate([[1, 2, 3], [1, 2, 3], [1, 2, 3]]));

    [Fact]
    public void CountsTheCellsHitAroundTheSampleMean()
    {
        // 两对对径样本，均值仍是 Center：命中 4 格。
        var samples = new List<double[]> { AtCell(6, 3), AtCell(0, 2), AtCell(9, 4), AtCell(3, 1) };

        Assert.Equal(4.0 / 72 * 100, SphericalCoverageEstimator.Estimate(samples), 9);
    }

    [Fact]
    public void EveryCellHitMeansFullCoverage()
    {
        var samples = new List<double[]>();
        for (int lon = 0; lon < 12; lon++)
            for (int lat = 0; lat < 6; lat++)
                samples.Add(AtCell(lon, lat));

        Assert.Equal(100, SphericalCoverageEstimator.Estimate(samples), 9);
    }

    [Fact]
    public void OnlyTheFirstThreeValuesAreUsed()
    {
        List<double[]> samples = [[.. AtCell(6, 3), 1e9], [.. AtCell(0, 2), -1e9]];

        Assert.Equal(2.0 / 72 * 100, SphericalCoverageEstimator.Estimate(samples), 9);
    }
}
