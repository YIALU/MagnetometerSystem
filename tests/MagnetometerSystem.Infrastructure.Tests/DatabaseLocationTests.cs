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
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(TargetDb)!, "*.migrating*"));
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
    public async Task MarkerWriteFailure_AfterCopy_StillUsesMigratedTarget()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();
        // 标记文件的位置被同名目录占住：数据库已复制就位，只有标记写不进去
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(TargetDb)!, DatabaseLocation.MigratedMarkerFileName));

        var db = new DatabaseInitializer(TargetDb, LegacyDb);
        await db.InitializeAsync();

        Assert.Equal(TargetDb, db.DatabasePath);
        Assert.DoesNotContain("失败", db.LocationNotice);
        Assert.Equal(1, await CountSessionsAsync(db, sessionId));
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
    public async Task ConcurrentFirstStart_BothInstancesEndUpOnTheMigratedTarget()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();

        // 两个实例同时首次启动：只有一个能改名成功，另一个不能因此回退到旧库
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var db = new DatabaseInitializer(TargetDb, LegacyDb);
            await db.InitializeAsync();
            return db;
        })));

        Assert.All(results, db => Assert.Equal(TargetDb, db.DatabasePath));
        Assert.Equal(1, await CountSessionsAsync(results[0], sessionId));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(TargetDb)!, "*.migrating*"));
    }

    [Fact]
    public async Task ResolveDatabasePath_AfterMigration_DoesNotFallBackToStaleLegacyCopy()
    {
        await CreateLegacyDatabaseWithSessionAsync();
        var dataDir = Path.GetDirectoryName(TargetDb)!;
        await new DatabaseInitializer(TargetDb, LegacyDb).InitializeAsync();
        SqliteConnection.ClearAllPools();

        // 已有权威数据库时直接选它，不做可写探测，也就不会因暂时不可写而回退到旧副本
        Assert.Equal(TargetDb, DatabaseLocation.ResolveDatabasePath(dataDir, Path.GetDirectoryName(LegacyDb)!));

        // 只有迁移标记（新库被删）也不回退
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(TargetDb + suffix);
        Assert.Equal(TargetDb, DatabaseLocation.ResolveDatabasePath(dataDir, Path.GetDirectoryName(LegacyDb)!));
    }

    [Fact]
    public void ResolveDatabasePath_FreshInstall_FallsBackWhenPreferredIsNotWritable()
    {
        var blocked = Path.Combine(_root, "blocked-file");
        File.WriteAllText(blocked, "");
        var fallback = Path.Combine(_root, "fallback");

        Assert.Equal(Path.Combine(fallback, DatabaseLocation.FileName),
            DatabaseLocation.ResolveDatabasePath(Path.Combine(blocked, "data"), fallback));
    }

    [Fact]
    public async Task AnotherInstanceRunning_DefersMigration_UntilAlone()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();

        var withOther = new DatabaseInitializer(TargetDb, LegacyDb) { AnotherInstanceRunning = true };
        await withOther.InitializeAsync();

        Assert.Equal(LegacyDb, withOther.DatabasePath);
        Assert.False(File.Exists(TargetDb));
        Assert.Equal(1, await CountSessionsAsync(withOther, sessionId));
    }

    [Fact]
    public async Task LegacyInUseByAnotherProcess_DefersMigration_ThenMigratesAfterRelease()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();
        var inUse = Path.Combine(Path.GetDirectoryName(LegacyDb)!, DatabaseLocation.LegacyInUseFileName);

        // 模拟另一个实例正以旧库运行：它共享打开占用文件
        using (new FileStream(inUse, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var deferred = new DatabaseInitializer(TargetDb, LegacyDb);
            await deferred.InitializeAsync();
            Assert.Equal(LegacyDb, deferred.DatabasePath);
            Assert.False(File.Exists(TargetDb));
        }

        // 对方退出后再启动才迁移（本进程刚才沿用旧库时也登记了占用，换一个独立的旧目录验证释放后的路径）
        var otherLegacyDir = Path.Combine(_root, "legacy2");
        Directory.CreateDirectory(otherLegacyDir);
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(LegacyDb + suffix)) File.Copy(LegacyDb + suffix, Path.Combine(otherLegacyDir, DatabaseLocation.FileName + suffix));
        var migrated = new DatabaseInitializer(TargetDb, Path.Combine(otherLegacyDir, DatabaseLocation.FileName));
        await migrated.InitializeAsync();
        Assert.Equal(TargetDb, migrated.DatabasePath);
        Assert.Equal(1, await CountSessionsAsync(migrated, sessionId));
    }

    [Fact]
    public async Task ConsumerOpeningBeforeInitialization_DoesNotCreateEmptyTarget()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();
        var db = new DatabaseInitializer(TargetDb, LegacyDb);

        // 迁移完成前有人提前打开（如迁移中途关窗触发保存设置）：只报错，不在新位置生成空库
        Directory.CreateDirectory(Path.GetDirectoryName(TargetDb)!);
        using (var early = new SqliteConnection(db.ConnectionString))
            Assert.Throws<SqliteException>(() => early.Open());
        Assert.False(File.Exists(TargetDb));

        await db.InitializeAsync();
        Assert.Equal(TargetDb, db.DatabasePath);
        Assert.Equal(1, await CountSessionsAsync(db, sessionId));
    }

    [Fact]
    public async Task MarkerWriteFailure_IsRetriedOnNextStart()
    {
        await CreateLegacyDatabaseWithSessionAsync();
        var markerPath = Path.Combine(Path.GetDirectoryName(TargetDb)!, DatabaseLocation.MigratedMarkerFileName);
        Directory.CreateDirectory(markerPath);
        await new DatabaseInitializer(TargetDb, LegacyDb).InitializeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(markerPath);

        // 存储恢复可写后再启动：补写标记，之后删掉新库也不会再复制旧副本
        await new DatabaseInitializer(TargetDb, LegacyDb).InitializeAsync();
        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(markerPath));
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(TargetDb + suffix);
        Assert.False(DatabaseLocation.MigrateIfNeeded(LegacyDb, TargetDb));
    }

    [Fact]
    public async Task FallbackWhenNotWritable_IsDecidedUnderGate_AndBlocksLaterMigration()
    {
        await CreateLegacyDatabaseWithSessionAsync();
        var blocked = Path.Combine(_root, "blocked-app");
        File.WriteAllText(blocked, "");

        // 程序目录不可写的实例在锁内回退旧库并登记占用
        var fallback = DatabaseLocation.MigrateOrDefer(LegacyDb, Path.Combine(blocked, "data", DatabaseLocation.FileName),
            anotherInstanceRunning: false, fallbackWhenNotWritable: true);
        Assert.Equal(DatabaseLocation.MigrationOutcome.FallbackNotWritable, fallback.Outcome);
        Assert.Equal(LegacyDb, fallback.DatabasePath);

        // 之后另一个能写程序目录的实例看到旧库仍在使用，推迟迁移
        var other = DatabaseLocation.MigrateOrDefer(LegacyDb, TargetDb, anotherInstanceRunning: false);
        Assert.Equal(DatabaseLocation.MigrationOutcome.DeferredLegacyInUse, other.Outcome);
        Assert.False(File.Exists(TargetDb));
    }

    [Fact]
    public async Task FallbackWhenNotWritable_UsesTargetMigratedByAnotherInstance()
    {
        var sessionId = await CreateLegacyDatabaseWithSessionAsync();
        await new DatabaseInitializer(TargetDb, LegacyDb).InitializeAsync();
        SqliteConnection.ClearAllPools();

        // 别的实例已迁移完成：即使本实例对程序目录的写入探测失败，也用新库而不回退旧副本
        var result = DatabaseLocation.MigrateOrDefer(LegacyDb, TargetDb, anotherInstanceRunning: false, fallbackWhenNotWritable: true);
        Assert.Equal(DatabaseLocation.MigrationOutcome.NotNeeded, result.Outcome);
        Assert.Equal(TargetDb, result.DatabasePath);
        var db = new DatabaseInitializer(TargetDb);
        await db.InitializeAsync();
        Assert.Equal(1, await CountSessionsAsync(db, sessionId));
    }

    [Fact]
    public async Task UninspectableTarget_DoesNotFallBackToLegacyCopy()
    {
        // Windows 上创建符号链接需要特权；用指向自身的链接制造“无法检查”（非“不存在”）的 I/O 错误
        if (OperatingSystem.IsWindows()) return;
        await CreateLegacyDatabaseWithSessionAsync();
        var dataDir = Path.GetDirectoryName(TargetDb)!;
        Directory.CreateDirectory(dataDir);
        File.CreateSymbolicLink(TargetDb, TargetDb);

        Assert.Equal(TargetDb, DatabaseLocation.ResolveDatabasePath(dataDir, Path.GetDirectoryName(LegacyDb)!));
        var result = DatabaseLocation.MigrateOrDefer(LegacyDb, TargetDb, anotherInstanceRunning: false, fallbackWhenNotWritable: true);
        Assert.Equal(DatabaseLocation.MigrationOutcome.NotNeeded, result.Outcome);
        Assert.Equal(TargetDb, result.DatabasePath);
        Assert.False(File.Exists(Path.Combine(dataDir, DatabaseLocation.MigratedMarkerFileName)));
    }

    [Fact]
    public async Task UninspectableLegacy_DoesNotCreateEmptyTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        // 旧库存在但无法检查（自指符号链接制造非“不存在”的 I/O 错误）：不能当全新安装在新位置建空库
        File.CreateSymbolicLink(LegacyDb, LegacyDb);

        var db = new DatabaseInitializer(TargetDb, LegacyDb);
        await Assert.ThrowsAnyAsync<Exception>(db.InitializeAsync);

        Assert.False(db.IsInitialized);
        Assert.Contains("失败", db.LocationNotice);
        Assert.False(File.Exists(TargetDb));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(TargetDb)!, DatabaseLocation.MigratedMarkerFileName)));
    }

    [Fact]
    public void FreshFallback_RegistersOccupancy_EvenWhenLegacyDirectoryDoesNotExistYet()
    {
        var blocked = Path.Combine(_root, "blocked-fresh");
        File.WriteAllText(blocked, "");
        var legacy = Path.Combine(_root, "new-legacy", DatabaseLocation.FileName);

        var result = DatabaseLocation.MigrateOrDefer(legacy, Path.Combine(blocked, "data", DatabaseLocation.FileName),
            anotherInstanceRunning: false, fallbackWhenNotWritable: true);

        Assert.Equal(DatabaseLocation.MigrationOutcome.FallbackNotWritable, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "new-legacy", DatabaseLocation.LegacyInUseFileName)));
    }

    [Fact]
    public async Task OccupancyRegistrationFailure_AbortsInitialization_InsteadOfUsingLegacyUnprotected()
    {
        await CreateLegacyDatabaseWithSessionAsync();
        // 占用文件的位置被同名目录占住，登记必然失败
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(LegacyDb)!, DatabaseLocation.LegacyInUseFileName));

        var db = new DatabaseInitializer(TargetDb, LegacyDb) { AnotherInstanceRunning = true };
        await Assert.ThrowsAsync<IOException>(db.InitializeAsync);

        Assert.False(db.IsInitialized);
        Assert.False(File.Exists(TargetDb));
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
