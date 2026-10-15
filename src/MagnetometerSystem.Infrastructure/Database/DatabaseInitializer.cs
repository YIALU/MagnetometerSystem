using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 数据库初始化器：创建 DB 文件、执行 Schema.sql（幂等）、启用 WAL 模式
/// </summary>
public class DatabaseInitializer
{
    private string _dbPath;

    /// <summary>数据库文件的完整路径（设置页只读显示）。</summary>
    public string DatabasePath => _dbPath;

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        ForeignKeys = true,
        DefaultTimeout = 2
    }.ToString();

    public string? LegacyDataWarning { get; private set; }

    /// <summary>
    /// 本次启动从旧位置迁移数据库或回退到旧位置的说明；无事发生时为 null。由启动流程写日志。
    /// </summary>
    public string? LocationNotice { get; private set; }

    /// <summary>
    /// 正式程序使用：数据库放在程序目录 data\，不可写时回退到 %LOCALAPPDATA%\MagnetometerSystem；
    /// 首次启动把旧位置的数据库迁移过来。
    /// </summary>
    public DatabaseInitializer()
        : this(
            DatabaseLocation.ResolveDatabasePath(DatabaseLocation.ProgramDataDirectory, DatabaseLocation.LegacyDirectory),
            Path.Combine(DatabaseLocation.LegacyDirectory, DatabaseLocation.FileName))
    {
    }

    public DatabaseInitializer(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <param name="dbPath">要使用的数据库。</param>
    /// <param name="legacyDbPath">旧版本的数据库位置；新位置没有数据库时从这里迁移。</param>
    public DatabaseInitializer(string dbPath, string? legacyDbPath)
    {
        _dbPath = dbPath;
        _legacyDbPath = legacyDbPath;
    }

    private readonly string? _legacyDbPath;

    /// <summary>
    /// 启动时已有其他程序实例在运行（由 App 根据单实例 mutex 判断）。此时若尚未迁移，
    /// 本次沿用旧数据库，避免复制后对方仍往旧库写入；全部关闭后再启动时迁移。
    /// </summary>
    public bool AnotherInstanceRunning { get; init; }

    public async Task InitializeAsync()
    {
        await MigrateFromLegacyLocationAsync();

        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await connection.ExecuteAsync("PRAGMA foreign_keys=ON;");

        LegacyDataWarning = await PreserveLegacyTablesIfNeededAsync(connection);

        await connection.ExecuteAsync(LoadSchemaSql());
        await EnsureSessionColumnAsync(connection, "channel_units", "TEXT");
        await EnsureSessionColumnAsync(connection, "legacy_data_table", "TEXT");
        var hasProfileUnit = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('orthogonality_profiles') WHERE name = 'unit'");
        if (hasProfileUnit == 0)
            await connection.ExecuteAsync("ALTER TABLE orthogonality_profiles ADD COLUMN unit TEXT NOT NULL DEFAULT '';");
    }

    /// <summary>
    /// 旧数据库迁移到新位置。复制失败时本次继续用旧数据库（数据可用，下次启动再试），
    /// 不在新位置建空库 —— 否则下次启动会认为已有数据库而不再迁移。
    /// </summary>
    private async Task MigrateFromLegacyLocationAsync()
    {
        if (_legacyDbPath is null) return;
        var legacy = _legacyDbPath;
        var target = _dbPath;
        var anotherInstance = AnotherInstanceRunning;
        try
        {
            switch (await Task.Run(() => DatabaseLocation.MigrateOrDefer(legacy, target, anotherInstance)))
            {
                case DatabaseLocation.MigrationOutcome.Migrated:
                    LocationNotice = $"已把旧数据库 {legacy} 复制到 {target}，旧文件保留未删除";
                    break;
                case DatabaseLocation.MigrationOutcome.DeferredLegacyInUse:
                    _dbPath = legacy;
                    LocationNotice = $"另有程序实例正在使用旧数据库，本次继续使用 {legacy}；关闭所有实例后再启动会迁移到 {target}";
                    break;
            }
        }
        catch (Exception ex)
        {
            _dbPath = legacy;
            DatabaseLocation.HoldLegacyInUse(legacy);
            LocationNotice = $"迁移旧数据库到 {target} 失败，本次继续使用 {legacy}: {ex.Message}";
        }
    }

    private static async Task EnsureSessionColumnAsync(SqliteConnection conn, string name, string type)
    {
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = @Name", new { Name = name });
        if (exists == 0) await conn.ExecuteAsync($"ALTER TABLE sessions ADD COLUMN {name} {type};");
    }

    private static async Task<string?> PreserveLegacyTablesIfNeededAsync(SqliteConnection conn)
    {
        var readingsExists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='readings'") > 0;
        if (!readingsExists) return null;

        var hasDataColumn = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('readings') WHERE name='data'") > 0;
        if (hasDataColumn) return null;

        await EnsureSessionColumnAsync(conn, "legacy_data_table", "TEXT");
        var suffix = Guid.NewGuid().ToString("N");
        var legacyTable = $"readings_legacy_{suffix}";
        var correctedExists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='corrected_readings'") > 0;
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync($"ALTER TABLE readings RENAME TO {legacyTable};", transaction: tx);
        if (correctedExists)
            await conn.ExecuteAsync($"ALTER TABLE corrected_readings RENAME TO corrected_readings_legacy_{suffix};", transaction: tx);
        await conn.ExecuteAsync(
            "UPDATE sessions SET legacy_data_table = @Table, notes = COALESCE(notes, '') || @Note",
            new { Table = legacyTable, Note = $"\n旧版原始数据完整保留于 {legacyTable}，需迁移后回放。" }, tx);
        tx.Commit();
        var warning = $"检测到旧版数据，已完整保留在 {legacyTable}。旧会话暂不能回放或导出，未删除原始数据。";
        System.Diagnostics.Trace.TraceWarning(warning);
        return warning;
    }

    private static string LoadSchemaSql()
    {
        const string fileName = "Schema.sql";
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"MagnetometerSystem.Infrastructure.Database.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var assemblyDir = Path.GetDirectoryName(assembly.Location) ?? ".";
        var filePath = Path.Combine(assemblyDir, "Database", fileName);
        if (File.Exists(filePath))
        {
            return File.ReadAllText(filePath);
        }

        var altPath = Path.Combine(AppContext.BaseDirectory, "Database", fileName);
        if (File.Exists(altPath))
        {
            return File.ReadAllText(altPath);
        }

        throw new FileNotFoundException(
            $"Schema script '{fileName}' not found as embedded resource '{resourceName}' or on disk.", fileName);
    }
}
