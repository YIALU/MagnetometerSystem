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

    public string ConnectionString => $"Data Source={_dbPath}";

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

        // 检测旧 schema：若 readings 表存在但缺少 data 列（旧版固定列设计），
        // 直接 drop 重建。旧采集数据无法迁移到新协议驱动模型 —— 用户已确认"旧库不保留"。
        await DropLegacyTablesIfNeededAsync(connection);

        await connection.ExecuteAsync(LoadSchemaSql());
    }

    private static async Task DropLegacyTablesIfNeededAsync(SqliteConnection conn)
    {
        var readingsExists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='readings'") > 0;
        if (!readingsExists) return;

        var hasDataColumn = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('readings') WHERE name='data'") > 0;
        if (hasDataColumn) return;

        // 旧 schema —— 丢弃 readings/corrected_readings/schema_version；sessions 元数据保留
        // （会显示为 0 条数据，用户可手动删除残留 session）
        System.Diagnostics.Trace.TraceWarning(
            "[DatabaseInitializer] 检测到旧 schema 的 readings 表（缺 data 列），将丢弃并重建。原始采集数据无法读取。");

        await conn.ExecuteAsync("DROP TABLE IF EXISTS corrected_readings;");
        await conn.ExecuteAsync("DROP TABLE IF EXISTS readings;");
        await conn.ExecuteAsync("DROP TABLE IF EXISTS schema_version;");

        // 清空残留 sessions 的 total_readings 计数
        await conn.ExecuteAsync("UPDATE sessions SET total_readings = 0;");
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
