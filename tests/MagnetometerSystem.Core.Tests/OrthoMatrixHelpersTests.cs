using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class OrthoMatrixHelpersTests
{
    [Fact]
    public void ToMatrixTakesTheFirstThreeValuesOfEachSample()
    {
        var matrix = OrthoMatrixHelpers.ToMatrix([[1, 2, 3, 99], [4, 5, 6]]);

        Assert.Equal(new double[,] { { 1, 2, 3 }, { 4, 5, 6 } }, matrix);
    }

    [Fact]
    public void ApplyAllCorrectsEveryRowWithTheFittedParameters()
    {
        var result = new OrthogonalityResult
        {
            Success = true,
            Parameters = new OrthogonalityParams { Unit = "nT", Offset = [1, 2, 3], CompensationMatrix = [2, 0, 0, 0, 2, 0, 0, 0, 2] },
        };
        var raw = new double[,] { { 1, 2, 3 }, { 2, 4, 6 } };

        var corrected = OrthoMatrixHelpers.ApplyAll(result, raw);

        // corrected = M * (raw - offset)
        Assert.Equal(new double[,] { { 0, 0, 0 }, { 2, 4, 6 } }, corrected);
        Assert.Equal(new double[,] { { 1, 2, 3 }, { 2, 4, 6 } }, raw);   // 原始矩阵不变
    }

    [Fact]
    public void ApplyAllRejectsNonFiniteSamplesLikeASinglePointCorrection()
    {
        var result = new OrthogonalityResult { Success = true, Parameters = new OrthogonalityParams { Unit = "nT" } };

        Assert.Throws<ArgumentException>(() => OrthoMatrixHelpers.ApplyAll(result, new double[,] { { 1, double.NaN, 3 } }));
    }
}
