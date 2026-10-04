using Dapper;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

public class SessionUnitsAndWriteRecoveryTests
{
    private static DatabaseInitializer Database() => new(Path.Combine(Path.GetTempPath(), $"magnetometer-units-{Guid.NewGuid():N}.db"));

    [Fact]
    public async Task NewSession_RetainsAllProtocolUnitsAfterStorageIsReopened()
    {
        var db = Database();
        await db.InitializeAsync();
        var protocol = ProtocolConfig.CreateZdzC08();
        string id;
        using (var storage = new SqliteStorageService(db, new DataBus()))
        {
            id = await storage.StartSessionAsync("mixed", new SensorConfig
            {
                ChannelCountOverride = protocol.DerivedChannelCount,
                ChannelNamesOverride = protocol.DerivedChannelNames.ToArray(),
                ChannelUnitsOverride = protocol.DerivedChannelUnits.ToArray(),
            }, new());
            await storage.SaveReadingsAsync([new() { SessionId = id, Timestamp = DateTime.Now,
                ChannelValues = Enumerable.Range(0, 21).Select(i => (double)i).ToArray() }]);
            await storage.EndSessionAsync(id);
        }
        using var reopened = new SqliteStorageService(db, new DataBus());
        var session = Assert.Single(await reopened.GetSessionsAsync());
        Assert.Equal(protocol.DerivedChannelNames, session.ChannelNames);
        Assert.Equal(protocol.DerivedChannelUnits, session.ChannelUnits);
        Assert.Equal(1, session.TotalReadings);
        Assert.Equal(21, Assert.Single(await reopened.GetReadingsAsync(id)).ChannelValues.Length);
    }

    [Fact]
    public async Task PreUnitSchema_IsMigratedIdempotentlyWithoutChangingSavedValues()
    {
        var db = Database();
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                CREATE TABLE sessions (id TEXT PRIMARY KEY, name TEXT NOT NULL, started_at TEXT NOT NULL,
                    ended_at TEXT, sensor_type TEXT NOT NULL, sample_rate REAL NOT NULL, channel_count INTEGER NOT NULL,
                    channel_names TEXT, device_info TEXT, connection_type TEXT, notes TEXT, total_readings INTEGER DEFAULT 0);
                CREATE TABLE readings (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                    timestamp TEXT NOT NULL, data TEXT NOT NULL);
                INSERT INTO sessions VALUES ('old', 'old', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z',
                    'TriaxialFluxgate', 1, 2, '["B","T"]', NULL, 'Serial', 'keep notes', 1);
                INSERT INTO readings (session_id, timestamp, data) VALUES ('old', '2026-01-01T00:00:00Z',
                    '{"values":{"B":12.5,"T":23.5}}');
                """);
        }
        await db.InitializeAsync();
        await db.InitializeAsync();
        using var storage = new SqliteStorageService(db, new DataBus());
        var session = Assert.Single(await storage.GetSessionsAsync());
        Assert.Equal(new[] { "未知单位", "未知单位" }, session.ChannelUnits);
        Assert.Equal("keep notes", session.Notes);
        Assert.Equal(1, session.TotalReadings);
        Assert.Equal(new[] { 12.5, 23.5 }, Assert.Single(await storage.GetReadingsAsync("old")).ChannelValues);
        using var check = new SqliteConnection(db.ConnectionString);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name='channel_units'"));
    }

    [Fact]
    public async Task FailedWrite_BlocksCompletionAndRetainsBatchForRetryWithoutDuplicates()
    {
        var db = Database();
        await db.InitializeAsync();
        using var storage = new SqliteStorageService(db, new DataBus());
        string id = await storage.StartSessionAsync("retry", new SensorConfig { ChannelCountOverride = 1, ChannelNamesOverride = ["B"] }, new());
        using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TRIGGER fail_save BEFORE INSERT ON readings BEGIN SELECT RAISE(FAIL, 'test write failure'); END;");
        try
        {
            await storage.SaveReadingsAsync(Enumerable.Range(0, 3).Select(i => new MagnetometerReading
                { SessionId = id, Timestamp = DateTime.Now.AddSeconds(i), ChannelValues = [i] }));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WaitForPendingWritesAsync());
            Assert.Contains("保存失败", error.Message);
            Assert.Equal(3, storage.PendingWriteCount);
            Assert.Null(Assert.Single(await storage.GetSessionsAsync()).EndedAt);
            Assert.Empty(await storage.GetReadingsAsync(id));
        }
        finally { await connection.ExecuteAsync("DROP TRIGGER fail_save;"); }
        await storage.RetryPendingWritesAsync();
        await storage.EndSessionAsync(id);
        Assert.Equal(0, storage.PendingWriteCount);
        Assert.Equal(new[] { 0d, 1d, 2d }, (await storage.GetReadingsAsync(id)).Select(r => r.ChannelValues[0]));
        Assert.Equal(3, Assert.Single(await storage.GetSessionsAsync()).TotalReadings);
    }

    [Fact]
    public async Task LockedDatabase_ReportsTimeoutAndLaterWaitCompletesRetainedWrites()
    {
        var db = Database();
        await db.InitializeAsync();
        using var storage = new SqliteStorageService(db, new DataBus());
        string id = await storage.StartSessionAsync("timeout", new SensorConfig(), new());
        using var blocker = new SqliteConnection(db.ConnectionString);
        await blocker.OpenAsync();
        using (var transaction = blocker.BeginTransaction())
        {
            await storage.SaveReadingsAsync([new() { SessionId = id, Timestamp = DateTime.Now, ChannelValues = [42] }]);
            await Assert.ThrowsAsync<TimeoutException>(() => storage.WaitForPendingWritesAsync(30));
            Assert.Equal(1, storage.PendingWriteCount);
            transaction.Rollback();
        }
        await storage.WaitForPendingWritesAsync();
        Assert.Equal(42, Assert.Single(await storage.GetReadingsAsync(id)).ChannelValues[0]);
        Assert.Equal(0, storage.PendingWriteCount);
    }
}
