using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class OrthogonalityParamsTests
{
    [Fact]
    public void Apply_IdentityMatrix_ReturnsOffsetCorrected()
    {
        var p = new OrthogonalityParams
        {
            Offset = new[] { 100.0, -50.0, 200.0 },
            CompensationMatrix = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }
        };

        var result = p.Apply(1100, 950, 1200);

        Assert.Equal(1000.0, result[0], precision: 6);
        Assert.Equal(1000.0, result[1], precision: 6);
        Assert.Equal(1000.0, result[2], precision: 6);
    }

    [Fact]
    public void Apply_ScalingMatrix_ScalesCorrectly()
    {
        var p = new OrthogonalityParams
        {
            Offset = new[] { 0.0, 0.0, 0.0 },
            CompensationMatrix = new double[] { 2, 0, 0, 0, 3, 0, 0, 0, 4 }
        };

        var result = p.Apply(10, 20, 30);

        Assert.Equal(20.0, result[0], precision: 6);
        Assert.Equal(60.0, result[1], precision: 6);
        Assert.Equal(120.0, result[2], precision: 6);
    }

    [Fact]
    public void GetMatrix_Returns3x3()
    {
        var p = new OrthogonalityParams();
        var m = p.GetMatrix();

        Assert.Equal(3, m.RowCount);
        Assert.Equal(3, m.ColumnCount);
        // 默认为单位矩阵
        Assert.Equal(1.0, m[0, 0]);
        Assert.Equal(0.0, m[0, 1]);
        Assert.Equal(1.0, m[1, 1]);
    }

    [Fact]
    public void DefaultValues_AreIdentity()
    {
        var p = new OrthogonalityParams();

        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, p.Offset);
        Assert.Equal(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, p.CompensationMatrix);
    }
}
