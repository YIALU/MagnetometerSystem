using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class FitQualityRatingTests
{
    private static OrthogonalityResult Result(string unit, double residualStd) => new()
    {
        Success = true,
        Parameters = new OrthogonalityParams { Unit = unit },
        Quality = new FitQuality { ResidualStd = residualStd },
    };

    [Theory]
    [InlineData("nT", 5, 5)]
    [InlineData("uT", 0.005, 5)]
    [InlineData("μT", 0.04, 40)]
    [InlineData("mT", 1e-4, 100)]
    [InlineData("T", 3e-7, 300)]
    public void ResidualIsConvertedToNanotesla(string unit, double residualStd, double expectedNt) =>
        Assert.Equal(expectedNt, FitQualityRating.ResidualStdInNt(Result(unit, residualStd)), 9);

    [Theory]
    [InlineData(9.99, "优秀", "ok", "残差标准差低于 10 nT")]
    [InlineData(10, "良好", "ok", "残差标准差低于 50 nT")]
    [InlineData(49.9, "良好", "ok", "残差标准差低于 50 nT")]
    [InlineData(50, "一般", "warn", "残差标准差低于 200 nT，建议增加姿态覆盖后重算")]
    [InlineData(199.9, "一般", "warn", "残差标准差低于 200 nT，建议增加姿态覆盖后重算")]
    [InlineData(200, "较差", "err", "残差标准差不低于 200 nT，建议检查数据后重新采集")]
    public void ThresholdsApplyToTheNanoteslaValue(double residualNt, string rating, string level, string hint)
    {
        // 同一物理残差在 uT 下评级相同。
        var inMicrotesla = Result("uT", residualNt / 1000);

        Assert.Equal(rating, FitQualityRating.Rate(Result("nT", residualNt)));
        Assert.Equal(rating, FitQualityRating.Rate(inMicrotesla));
        Assert.Equal(level, FitQualityRating.Level(residualNt));
        Assert.Equal(hint, FitQualityRating.Hint(residualNt));
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("gauss", 5)]
    [InlineData("nT", double.NaN)]
    [InlineData("nT", -1)]
    public void UnknownUnitOrInvalidResidualCannotBeRated(string unit, double residualStd)
    {
        var residualNt = FitQualityRating.ResidualStdInNt(Result(unit, residualStd));

        Assert.True(double.IsNaN(residualNt));
        Assert.Equal("未知", FitQualityRating.Rate(Result(unit, residualStd)));
        Assert.Equal("err", FitQualityRating.Level(residualNt));
        Assert.Equal("单位未知，不能评级", FitQualityRating.Hint(residualNt));
    }

    [Fact]
    public void MissingResultIsShownAsDash()
    {
        Assert.Equal("—", FitQualityRating.Rate(null));
        Assert.True(double.IsNaN(FitQualityRating.ResidualStdInNt(null)));
    }
}
