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
        await connection.ExecuteAsync(LoadSchemaSql());
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
