using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 数据库初始化器：创建 DB 文件、执行 Schema.sql（幂等）、启用 WAL 模式
/// </summary>
public class DatabaseInitializer
{
    private readonly string _dbPath;

    /// <summary>数据库文件的完整路径（设置页只读显示）。</summary>
    public string DatabasePath => _dbPath;

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        ForeignKeys = true,
        DefaultTimeout = 2
    }.ToString();

    public string? LegacyDataWarning { get; private set; }

    public DatabaseInitializer()
    {
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MagnetometerSystem",
            "magnetometer.db");
    }

    public DatabaseInitializer(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync()
    {
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
