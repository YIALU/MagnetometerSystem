using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 数据库初始化器：创建 DB 文件、执行 DDL、启用 WAL 模式
/// </summary>
public class DatabaseInitializer
{
    private readonly string _dbPath;

    /// <summary>获取数据库连接字符串</summary>
    public string ConnectionString => $"Data Source={_dbPath}";

    /// <summary>
    /// 使用默认路径创建初始化器
    /// </summary>
    public DatabaseInitializer()
    {
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MagnetometerSystem",
            "magnetometer.db");
    }

    /// <summary>
    /// 使用自定义路径创建初始化器（用于测试）
    /// </summary>
    public DatabaseInitializer(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// 初始化数据库：创建目录、创建表、启用 WAL 模式。
    /// 应在应用启动时调用一次。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. 确保目录存在
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 2. 打开连接，启用 WAL 和外键约束
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await connection.ExecuteAsync("PRAGMA foreign_keys=ON;");

        // 3. 读取并执行迁移脚本
        var v1Sql = LoadMigrationSql("V1_InitialSchema.sql");
        await connection.ExecuteAsync(v1Sql);

        var v2Sql = LoadMigrationSql("V2_CalibrationTables.sql");
        await connection.ExecuteAsync(v2Sql);

        var v3Sql = LoadMigrationSql("V3_CorrectedReadings.sql");
        await connection.ExecuteAsync(v3Sql);

        try
        {
            var v4Sql = LoadMigrationSql("V4_AddGpsCoordinates.sql");
            await connection.ExecuteAsync(v4Sql);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // 列已存在，忽略错误
        }

        var v5Sql = LoadMigrationSql("V5_AddOrthogonalityTables.sql");
        await connection.ExecuteAsync(v5Sql);

        try
        {
            var v6Sql = LoadMigrationSql("V6_AddOriginalChannelValues.sql");
            await connection.ExecuteAsync(v6Sql);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // 列已存在，忽略错误
        }
    }

    private static string LoadMigrationSql(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"MagnetometerSystem.Infrastructure.Database.Migrations.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // 回退：从文件系统加载
        var assemblyDir = Path.GetDirectoryName(assembly.Location) ?? ".";
        var filePath = Path.Combine(assemblyDir, "Database", "Migrations", fileName);
        if (File.Exists(filePath))
        {
            return File.ReadAllText(filePath);
        }

        // 再回退：相对于程序集目录查找
        var altPath = Path.Combine(AppContext.BaseDirectory, "Database", "Migrations", fileName);
        if (File.Exists(altPath))
        {
            return File.ReadAllText(altPath);
        }

        throw new FileNotFoundException(
            $"Migration script '{fileName}' not found as embedded resource '{resourceName}' or on disk.", fileName);
    }
}
