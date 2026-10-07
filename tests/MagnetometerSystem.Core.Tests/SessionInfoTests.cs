using MagnetometerSystem.Core.Storage;
using Xunit;

namespace MagnetometerSystem.Core.Tests;

public class SessionInfoTests
{
    [Fact]
    public void DurationUsesUtcInstantsAcrossDaylightSavingChanges()
    {
        // 秋季回拨：本地钟面上结束早于开始，真实经过 30 分钟。
        var startUtc = new DateTime(2026, 11, 1, 5, 45, 0, DateTimeKind.Utc);
        var session = new SessionInfo
        {
            StartedAt = new DateTime(2026, 11, 1, 1, 45, 0, DateTimeKind.Local),
            EndedAt = new DateTime(2026, 11, 1, 1, 15, 0, DateTimeKind.Local),
            StartedAtUtc = startUtc,
            EndedAtUtc = startUtc.AddMinutes(30),
        };
        Assert.Equal(TimeSpan.FromMinutes(30), session.Duration);
    }

    [Fact]
    public void WithoutStoredUtcInstantsDurationFallsBackToLocalTimes()
    {
        var start = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
        var session = new SessionInfo { StartedAt = start, EndedAt = start.AddSeconds(90) };
        Assert.Equal(TimeSpan.FromSeconds(90), session.Duration);
        Assert.Null(new SessionInfo { StartedAt = start }.Duration);
    }
}
