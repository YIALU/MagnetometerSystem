using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class CorrectedReadingTests
{
    [Fact]
    public void CorrectedReading_PreservesOriginalId()
    {
        var original = new MagnetometerReading
        {
            Id = 42,
            SessionId = "session1",
            Timestamp = DateTime.UtcNow,
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = new[] { 100.0, 200.0, 300.0 },
            IsCalibrated = false,
            IsOrthogonalityCorrected = false
        };

        var corrected = CorrectedReading.FromOriginal(original, new[] { 99.0, 198.0, 297.0 }, "profile-1");

        Assert.Equal(42, corrected.OriginalReadingId);
        Assert.Equal("session1", corrected.SessionId);
        Assert.Equal("profile-1", corrected.CorrectionProfileId);
        Assert.Equal(99.0, corrected.CorrectedValues[0]);
        Assert.Equal(198.0, corrected.CorrectedValues[1]);
        Assert.Equal(297.0, corrected.CorrectedValues[2]);
        Assert.True(corrected.IsOrthogonalityCorrected);
    }

    [Fact]
    public void CorrectedReading_ComputesTotalField()
    {
        var original = new MagnetometerReading
        {
            Id = 1,
            SessionId = "s1",
            Timestamp = DateTime.UtcNow,
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = new[] { 3.0, 4.0, 0.0 }
        };

        var corrected = CorrectedReading.FromOriginal(original, new[] { 3.0, 4.0, 0.0 }, "p1");

        Assert.NotNull(corrected.CorrectedTotalField);
        Assert.Equal(5.0, corrected.CorrectedTotalField!.Value, 10);
    }

    [Fact]
    public void CorrectedReading_NoTotalFieldForLessThan3Channels()
    {
        var original = new MagnetometerReading
        {
            Id = 2,
            SessionId = "s2",
            Timestamp = DateTime.UtcNow,
            SensorType = SensorType.SingleAxisFluxgate,
            ChannelValues = new[] { 50000.0 }
        };

        var corrected = CorrectedReading.FromOriginal(original, new[] { 49999.0 }, "p1");

        Assert.Null(corrected.CorrectedTotalField);
    }

    [Fact]
    public void CorrectedReading_PreservesTimestamp()
    {
        var timestamp = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var original = new MagnetometerReading
        {
            Id = 3,
            SessionId = "s3",
            Timestamp = timestamp,
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = new[] { 1.0, 2.0, 3.0 }
        };

        var corrected = CorrectedReading.FromOriginal(original, new[] { 1.1, 2.1, 3.1 }, "p2");

        Assert.Equal(timestamp, corrected.Timestamp);
    }

    [Fact]
    public void CorrectedReading_SetsCorrectedAtToUtcNow()
    {
        var before = DateTime.UtcNow;

        var original = new MagnetometerReading
        {
            Id = 4,
            SessionId = "s4",
            Timestamp = DateTime.UtcNow,
            SensorType = SensorType.TriaxialFluxgate,
            ChannelValues = new[] { 1.0, 2.0, 3.0 }
        };

        var corrected = CorrectedReading.FromOriginal(original, new[] { 1.0, 2.0, 3.0 }, "p1");

        var after = DateTime.UtcNow;
        Assert.InRange(corrected.CorrectedAt, before, after);
    }
}
