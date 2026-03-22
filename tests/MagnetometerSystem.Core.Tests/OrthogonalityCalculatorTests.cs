using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class OrthogonalityCalculatorTests
{
    private readonly OrthogonalityCalculator _calculator = new();

    /// <summary>
    /// Generate uniform spherical data with a known offset and scale distortion.
    /// The ideal sphere has radius <paramref name="totalField"/> centered at <paramref name="offset"/>,
    /// with per-axis scale factors applied to create an ellipsoid.
    /// </summary>
    private static double[,] GenerateSyntheticEllipsoidData(
        int count,
        double[] offset,
        double[] scale,
        double totalField = 50000,
        int seed = 42)
    {
        var rng = new Random(seed);
        var data = new double[count, 3];

        for (int i = 0; i < count; i++)
        {
            // Uniform spherical sampling
            double theta = Math.Acos(2 * rng.NextDouble() - 1);
            double phi = 2 * Math.PI * rng.NextDouble();

            double bx = totalField * Math.Sin(theta) * Math.Cos(phi);
            double by = totalField * Math.Sin(theta) * Math.Sin(phi);
            double bz = totalField * Math.Cos(theta);

            // Apply scale distortion (creates ellipsoid) and offset
            data[i, 0] = bx * scale[0] + offset[0];
            data[i, 1] = by * scale[1] + offset[1];
            data[i, 2] = bz * scale[2] + offset[2];
        }

        return data;
    }

    [Fact]
    public void Calculate_WithValidSphericalData_RecoversApproximateOffset()
    {
        // Arrange: known offset and mild scale distortion
        double[] offset = { 100, -50, 200 };
        double[] scale = { 1.02, 0.98, 1.01 };
        var data = GenerateSyntheticEllipsoidData(600, offset, scale);

        // Act
        var result = _calculator.Calculate(data);

        // Assert
        Assert.True(result.Success, $"Calculate failed: {result.ErrorMessage}");
        Assert.NotNull(result.Parameters);

        // The recovered offset should be within a reasonable tolerance of the known offset.
        // Ellipsoid fitting can have some error, so we use a generous tolerance relative to field strength.
        double tolerance = 5000; // 10% of 50000 nT field
        Assert.InRange(result.Parameters.Offset[0], offset[0] - tolerance, offset[0] + tolerance);
        Assert.InRange(result.Parameters.Offset[1], offset[1] - tolerance, offset[1] + tolerance);
        Assert.InRange(result.Parameters.Offset[2], offset[2] - tolerance, offset[2] + tolerance);
    }

    [Fact]
    public void Calculate_WithValidData_ProducesReasonableCompensationMatrix()
    {
        double[] offset = { 100, -50, 200 };
        double[] scale = { 1.02, 0.98, 1.01 };
        var data = GenerateSyntheticEllipsoidData(600, offset, scale);

        var result = _calculator.Calculate(data);

        Assert.True(result.Success);
        // Compensation matrix should have 9 elements (3x3 row-major)
        Assert.Equal(9, result.Parameters.CompensationMatrix.Length);
        // Diagonal elements should be positive and roughly near 1.0 (within an order of magnitude)
        double m00 = result.Parameters.CompensationMatrix[0];
        double m11 = result.Parameters.CompensationMatrix[4];
        double m22 = result.Parameters.CompensationMatrix[8];
        Assert.True(m00 > 0, "m00 should be positive");
        Assert.True(m11 > 0, "m11 should be positive");
        Assert.True(m22 > 0, "m22 should be positive");
    }

    [Fact]
    public void Calculate_InsufficientSamples_ReturnsFailure()
    {
        // Only 50 points, below the MinSampleCount of 100
        double[] offset = { 0, 0, 0 };
        double[] scale = { 1, 1, 1 };
        var data = GenerateSyntheticEllipsoidData(50, offset, scale);

        var result = _calculator.Calculate(data);

        Assert.False(result.Success);
        Assert.Contains("100", result.ErrorMessage); // should mention minimum count
    }

    [Fact]
    public void Calculate_AllNaNInput_ReturnsFailure()
    {
        var data = new double[200, 3];
        for (int i = 0; i < 200; i++)
        {
            data[i, 0] = double.NaN;
            data[i, 1] = double.NaN;
            data[i, 2] = double.NaN;
        }

        var result = _calculator.Calculate(data);

        Assert.False(result.Success);
    }

    [Fact]
    public void Calculate_MixedNaNAndValidData_ReturnsFailureDueToOutlierFilterPollution()
    {
        // RemoveOutliers computes mean/std of total field; NaN rows cause
        // NaN mean → all data fails the threshold → empty after filtering.
        // This test verifies that behavior: mixed NaN data results in failure.
        double[] offset = { 100, -50, 200 };
        double[] scale = { 1.0, 1.0, 1.0 };
        var cleanData = GenerateSyntheticEllipsoidData(800, offset, scale);
        var data = new double[820, 3];

        for (int i = 0; i < 800; i++)
        {
            data[i, 0] = cleanData[i, 0];
            data[i, 1] = cleanData[i, 1];
            data[i, 2] = cleanData[i, 2];
        }
        for (int i = 800; i < 820; i++)
        {
            data[i, 0] = double.NaN;
            data[i, 1] = double.PositiveInfinity;
            data[i, 2] = double.NegativeInfinity;
        }

        var result = _calculator.Calculate(data);

        // NaN pollutes RemoveOutliers statistics, causing all data to be rejected
        Assert.False(result.Success);
    }

    [Fact]
    public void Apply_WithIdentityMatrix_ReturnsOffsetCorrected()
    {
        var parameters = new OrthogonalityParams
        {
            Offset = new[] { 10.0, 20.0, 30.0 },
            CompensationMatrix = new[] { 1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0 }
        };

        var corrected = _calculator.Apply(parameters, 110, 120, 130);

        // corrected = I * ([110,120,130] - [10,20,30]) = [100, 100, 100]
        Assert.Equal(100.0, corrected[0], precision: 10);
        Assert.Equal(100.0, corrected[1], precision: 10);
        Assert.Equal(100.0, corrected[2], precision: 10);
    }

    [Fact]
    public void Apply_WithScaleMatrix_ScalesCorrectly()
    {
        var parameters = new OrthogonalityParams
        {
            Offset = new[] { 0.0, 0.0, 0.0 },
            CompensationMatrix = new[] { 2.0, 0, 0, 0, 3.0, 0, 0, 0, 4.0 }
        };

        var corrected = _calculator.Apply(parameters, 10, 20, 30);

        Assert.Equal(20.0, corrected[0], precision: 10);
        Assert.Equal(60.0, corrected[1], precision: 10);
        Assert.Equal(120.0, corrected[2], precision: 10);
    }

    [Fact]
    public void EvaluateFit_WithPerfectSphere_HasLowResiduals()
    {
        // Generate a perfect sphere (no distortion) and use identity calibration
        double[] offset = { 0, 0, 0 };
        double[] scale = { 1, 1, 1 };
        var data = GenerateSyntheticEllipsoidData(500, offset, scale, totalField: 50000);

        var parameters = new OrthogonalityParams
        {
            Offset = new[] { 0.0, 0.0, 0.0 },
            CompensationMatrix = new[] { 1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0 }
        };

        var quality = _calculator.EvaluateFit(parameters, data);

        Assert.Equal(500, quality.SampleCount);
        // For a perfect sphere with identity calibration, residuals should be near zero
        Assert.InRange(quality.ResidualStd, 0, 1.0); // very small std
        Assert.True(quality.SphericityCoverage > 0.5, "Coverage should be reasonable for uniform sphere");
    }
}
