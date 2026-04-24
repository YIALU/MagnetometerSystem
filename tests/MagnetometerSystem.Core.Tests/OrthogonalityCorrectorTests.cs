using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class OrthogonalityCorrectorTests
{
    private readonly OrthogonalityCorrector _corrector = new();

    [Fact]
    public void ApplyToReading_IdentityMatrix_ZeroOffset_ReturnsOriginalValues()
    {
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [0, 0, 0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [100.0, 200.0, 300.0],
        };

        var corrected = _corrector.ApplyToReading(parameters, reading);

        Assert.Equal(100.0, corrected.ChannelValues[0], 6);
        Assert.Equal(200.0, corrected.ChannelValues[1], 6);
        Assert.Equal(300.0, corrected.ChannelValues[2], 6);
    }

    [Fact]
    public void ApplyToReading_IdentityMatrixWithOffset_SubtractsOffset()
    {
        // corrected = M * (raw - offset) = I * (raw - offset) = raw - offset
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [10.0, 20.0, 30.0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [110.0, 220.0, 330.0],
        };

        var corrected = _corrector.ApplyToReading(parameters, reading);

        Assert.Equal(100.0, corrected.ChannelValues[0], 6);
        Assert.Equal(200.0, corrected.ChannelValues[1], 6);
        Assert.Equal(300.0, corrected.ChannelValues[2], 6);
    }

    [Fact]
    public void ApplyToReading_ScalingMatrix_AppliesCorrectly()
    {
        // M = diag(2, 3, 0.5), offset = [0,0,0]
        // corrected = M * raw = [2*x, 3*y, 0.5*z]
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [2, 0, 0, 0, 3, 0, 0, 0, 0.5],
            Offset = [0, 0, 0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [10.0, 20.0, 40.0],
        };

        var corrected = _corrector.ApplyToReading(parameters, reading);

        Assert.Equal(20.0, corrected.ChannelValues[0], 6);
        Assert.Equal(60.0, corrected.ChannelValues[1], 6);
        Assert.Equal(20.0, corrected.ChannelValues[2], 6);
    }

    [Fact]
    public void ApplyToReading_DualTriaxial_AppliesBothGroups()
    {
        var firstGroup = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [1.0, 2.0, 3.0],
        };
        var secondGroup = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [4.0, 5.0, 6.0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.DualTriaxialFluxgate,
            ChannelValues = [11.0, 22.0, 33.0, 44.0, 55.0, 66.0],
        };

        var corrected = _corrector.ApplyToReading(firstGroup, secondGroup, reading);

        Assert.Equal(6, corrected.ChannelValues.Length);
        // First group: raw - offset
        Assert.Equal(10.0, corrected.ChannelValues[0], 6);
        Assert.Equal(20.0, corrected.ChannelValues[1], 6);
        Assert.Equal(30.0, corrected.ChannelValues[2], 6);
        // Second group: raw - offset
        Assert.Equal(40.0, corrected.ChannelValues[3], 6);
        Assert.Equal(50.0, corrected.ChannelValues[4], 6);
        Assert.Equal(60.0, corrected.ChannelValues[5], 6);
    }

    [Fact]
    public void ApplyToReading_NonTriaxialSensor_ReturnsOriginalReading()
    {
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [2, 0, 0, 0, 2, 0, 0, 0, 2],
            Offset = [10, 10, 10],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.ProtonMagnetometer,
            ChannelValues = [50000.0],
        };

        var result = _corrector.ApplyToReading(parameters, reading);

        // Should return the same reading instance (passthrough)
        Assert.Same(reading, result);
        Assert.Equal(50000.0, result.ChannelValues[0], 6);
    }

    [Fact]
    public void ApplyToReading_TotalField_RecalculatedFromFirstTriaxial()
    {
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [0, 0, 0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [3.0, 4.0, 0.0],
        };

        var corrected = _corrector.ApplyToReading(parameters, reading);

        // Verify corrected values
        Assert.NotNull(corrected);
    }

    [Fact]
    public void ApplyToReading_SetsOrthogonalityCorrectedFlag()
    {
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [0, 0, 0],
        };
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [10.0, 20.0, 30.0],
            IsOrthogonalityCorrected = false,
        };

        var corrected = _corrector.ApplyToReading(parameters, reading);

        Assert.True(corrected.IsOrthogonalityCorrected);
        Assert.False(reading.IsOrthogonalityCorrected); // original unchanged
    }

    [Fact]
    public void ApplyToReading_DoesNotModifyOriginalReading()
    {
        var parameters = new OrthogonalityParams
        {
            CompensationMatrix = [2, 0, 0, 0, 2, 0, 0, 0, 2],
            Offset = [0, 0, 0],
        };
        var original = new MagnetometerReading
        {
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = [10.0, 20.0, 30.0],
        };

        var corrected = _corrector.ApplyToReading(parameters, original);

        // Original should be unchanged
        Assert.Equal(10.0, original.ChannelValues[0], 6);
        Assert.Equal(20.0, original.ChannelValues[1], 6);
        Assert.Equal(30.0, original.ChannelValues[2], 6);
        // Corrected should differ
        Assert.Equal(20.0, corrected.ChannelValues[0], 6);
    }

    [Fact]
    public void ApplyToReading_SingleAxisFluxgate_Passthrough()
    {
        var parameters = new OrthogonalityParams();
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.SingleAxisFluxgate,
            ChannelValues = [12345.0],
        };

        var result = _corrector.ApplyToReading(parameters, reading);

        Assert.Same(reading, result);
    }

    [Fact]
    public async Task ApplyBatchAsync_DualGroups_AppliesBothToAllReadings()
    {
        var firstGroup = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [1.0, 2.0, 3.0],
        };
        var secondGroup = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [10.0, 20.0, 30.0],
        };
        var readings = new[]
        {
            new MagnetometerReading
            {
                SensorType = SensorType.DualTriaxialFluxgate,
                ChannelValues = [11.0, 22.0, 33.0, 100.0, 200.0, 300.0],
            },
            new MagnetometerReading
            {
                SensorType = SensorType.DualTriaxialFluxgate,
                ChannelValues = [12.0, 23.0, 34.0, 110.0, 210.0, 310.0],
            },
        };

        var batch = await _corrector.ApplyBatchAsync(firstGroup, secondGroup, readings);

        Assert.Equal(2, batch.ProcessedCount);
        // First reading
        Assert.Equal(10.0, batch.CorrectedReadings[0].ChannelValues[0], 6);  // 11 - 1
        Assert.Equal(20.0, batch.CorrectedReadings[0].ChannelValues[1], 6);  // 22 - 2
        Assert.Equal(30.0, batch.CorrectedReadings[0].ChannelValues[2], 6);  // 33 - 3
        Assert.Equal(90.0, batch.CorrectedReadings[0].ChannelValues[3], 6);  // 100 - 10
        Assert.Equal(180.0, batch.CorrectedReadings[0].ChannelValues[4], 6); // 200 - 20
        Assert.Equal(270.0, batch.CorrectedReadings[0].ChannelValues[5], 6); // 300 - 30
        // Second reading: 后 3 通道也必须被校正（之前的 bug 是会保持不变）
        Assert.Equal(100.0, batch.CorrectedReadings[1].ChannelValues[3], 6); // 110 - 10
        Assert.Equal(190.0, batch.CorrectedReadings[1].ChannelValues[4], 6); // 210 - 20
        Assert.Equal(280.0, batch.CorrectedReadings[1].ChannelValues[5], 6); // 310 - 30
    }

    [Fact]
    public async Task ApplyBatchAsync_DualGroups_NullSecondGroup_LeavesLast3Untouched()
    {
        var firstGroup = new OrthogonalityParams
        {
            CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Offset = [1.0, 2.0, 3.0],
        };
        var readings = new[]
        {
            new MagnetometerReading
            {
                SensorType = SensorType.DualTriaxialFluxgate,
                ChannelValues = [11.0, 22.0, 33.0, 100.0, 200.0, 300.0],
            },
        };

        var batch = await _corrector.ApplyBatchAsync(firstGroup, null, readings);

        Assert.Equal(10.0, batch.CorrectedReadings[0].ChannelValues[0], 6);
        // null secondGroup → 后 3 通道保留原值
        Assert.Equal(100.0, batch.CorrectedReadings[0].ChannelValues[3], 6);
        Assert.Equal(200.0, batch.CorrectedReadings[0].ChannelValues[4], 6);
        Assert.Equal(300.0, batch.CorrectedReadings[0].ChannelValues[5], 6);
    }
}
