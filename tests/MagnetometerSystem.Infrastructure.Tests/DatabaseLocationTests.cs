using Dapper;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

/// <summary>数据库从旧位置（%LOCALAPPDATA%）迁移到程序目录 data\ 的行为，使用真实临时 SQLite 文件。</summary>
public sealed class DatabaseLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"db-location-{Guid.NewGuid():N}");
    private string LegacyDb => Path.Combine(_root, "legacy", DatabaseLocation.FileName);
    private string TargetDb => Path.Combine(_root, "app", "data", DatabaseLocation.FileName);

    public DatabaseLocationTests() => Directory.CreateDirectory(Path.Combine(_root, "legacy"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>用正式初始化器建一个旧库并写入一个会话，模拟旧版本留下的数据。</summary>
    private async Task<string> CreateLegacyDatabaseWithSessionAsync()
    {
        var legacy = new DatabaseInitializer(LegacyDb);
        await legacy.InitializeAsync();
        var sessionId = Guid.NewGuid().ToString();
        using var conn = new SqliteConnection(legacy.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO sessions (id, name, started_at, sensor_type, sample_rate, channel_count) VALUES (@Id, '旧会话', '2026-10-01T00:00:00Z', 'Custom', 10, 3)",
            new { Id = sessionId });
        SqliteConnection.ClearAllPools();
        return sessionId;
    }

    private static async Task<long> CountSessionsAsync(DatabaseInitializer db, string id)
    {
        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sessions WHERE id = @Id", new { Id = id });
    }

    [Fact]
    public async Task FirstStart_CopiesLegacyDatabaseToTarget_AndKeepsLegacyFile()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();

        var db = new DatabaseInitializer(TargetDb, LegacyDb);
        await db.InitializeAsync();

        Assert.Equal(TargetDb, db.DatabasePath);
        Assert.NotNull(db.LocationNotice);
        Assert.Equal(1, await CountSessionsAsync(db, sessionId));
        Assert.True(File.Exists(LegacyDb));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(TargetDb)!, DatabaseLocation.MigratedMarkerFileName)));
        Assert.False(File.Exists(TargetDb + ".migrating"));
    }

    [Fact]
    public async Task ExistingTarget_IsNotOverwrittenByLegacy()
    {
        var legacySession = await CreateLegacyDatabaseWithSessionAsync();
        var existing = new DatabaseInitializer(TargetDb);
        await existing.InitializeAsync();
        SqliteConnection.ClearAllPools();

        var db = new DatabaseInitializer(TargetDb, LegacyDb);
        await db.InitializeAsync();

        Assert.Null(db.LocationNotice);
        Assert.Equal(0, await CountSessionsAsync(db, legacySession));
    }

    [Fact]
    public async Task AfterMigration_DeletedTargetIsNotRecopied()
    {
        await CreateLegacyDatabaseWithSessionAsync();
        await new DatabaseInitializer(TargetDb, LegacyDb).InitializeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(TargetDb + suffix);

        Assert.False(DatabaseLocation.MigrateIfNeeded(LegacyDb, TargetDb));
        Assert.False(File.Exists(TargetDb));
    }

    [Fact]
    public async Task NoLegacyDatabase_CreatesFreshTarget()
    {
        var db = new DatabaseInitializer(TargetDb, LegacyDb);
        await db.InitializeAsync();

        Assert.Null(db.LocationNotice);
        Assert.True(File.Exists(TargetDb));
    }

    [Fact]
    public async Task MigrationFailure_KeepsUsingLegacyDatabase_AndLeavesNoEmptyTarget()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();
        // 目标目录位置被一个普通文件占住，复制必然失败
        var blockedDir = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedDir, "");
        var target = Path.Combine(blockedDir, DatabaseLocation.FileName);

        var db = new DatabaseInitializer(target, LegacyDb);
        await db.InitializeAsync();

        Assert.Equal(LegacyDb, db.DatabasePath);
        Assert.Contains("失败", db.LocationNotice);
        Assert.Equal(1, await CountSessionsAsync(db, sessionId));
    }

    [Fact]
    public void ResolveDirectory_FallsBackWhenPreferredIsNotWritable()
    {
        var blocked = Path.Combine(_root, "not-a-dir");
        File.WriteAllText(blocked, "");
        var fallback = Path.Combine(_root, "fallback");

        Assert.Equal(fallback, DatabaseLocation.ResolveDirectory(Path.Combine(blocked, "data"), fallback));

        var preferred = Path.Combine(_root, "ok", "data");
        Assert.Equal(preferred, DatabaseLocation.ResolveDirectory(preferred, fallback));
        Assert.Empty(Directory.GetFiles(preferred));
    }
}
