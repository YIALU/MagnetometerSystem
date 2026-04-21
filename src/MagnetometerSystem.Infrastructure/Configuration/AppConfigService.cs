using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MagnetometerSystem.Infrastructure.Database;

namespace MagnetometerSystem.Infrastructure.Configuration;

/// <summary>
/// 基于 SQLite 的应用配置持久化服务实现。
/// 使用 settings 表（由 V1 迁移脚本创建）存储键值对配置。
/// </summary>
public class AppConfigService : IAppConfigService
{
    private readonly DatabaseInitializer _dbInit;

    public AppConfigService(DatabaseInitializer dbInit)
    {
        _dbInit = dbInit;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var json = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = @Key",
            new { Key = key });

        if (json is null)
            return default;

        return JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        var updatedAt = DateTime.UtcNow.ToString("O");

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            INSERT INTO settings (key, value, updated_at) VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(key) DO UPDATE SET value = @Value, updated_at = @UpdatedAt
            """;

        await conn.ExecuteAsync(sql, new { Key = key, Value = json, UpdatedAt = updatedAt });
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var settings = new AppSettings();

        // 一次性查询所有配置键，避免 8 次独立连接
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM settings WHERE key IN @Keys",
            new { Keys = new[]
            {
                "connection.defaultPortName",
                "connection.defaultBaudRate",
                "connection.defaultIpAddress",
                "connection.defaultPort",
                "storage.dataPath",
                "storage.autoSave",
                "chart.refreshRate",
                "ui.theme",
            }});

        var map = rows.ToDictionary(r => r.Key, r => r.Value);

        T? Get<T>(string k)
        {
            if (!map.TryGetValue(k, out var json) || json is null) return default;
            try { return JsonSerializer.Deserialize<T>(json); }
            catch { return default; }
        }

        settings.DefaultPortName = Get<string>("connection.defaultPortName");

        var baudRate = Get<int?>("connection.defaultBaudRate");
        if (baudRate is > 0) settings.DefaultBaudRate = baudRate.Value;

        settings.DefaultIpAddress = Get<string>("connection.defaultIpAddress");

        var port = Get<int?>("connection.defaultPort");
        if (port is > 0) settings.DefaultPort = port.Value;

        settings.DataStoragePath = Get<string>("storage.dataPath") ?? "";

        var autoSave = Get<bool?>("storage.autoSave");
        if (autoSave.HasValue) settings.AutoSaveEnabled = autoSave.Value;

        var refreshRate = Get<int?>("chart.refreshRate");
        if (refreshRate is > 0) settings.ChartRefreshRate = refreshRate.Value;

        settings.ThemeName = Get<string>("ui.theme") ?? "Default";

        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await SetAsync("connection.defaultPortName", settings.DefaultPortName);
        await SetAsync("connection.defaultBaudRate", settings.DefaultBaudRate);
        await SetAsync("connection.defaultIpAddress", settings.DefaultIpAddress);
        await SetAsync("connection.defaultPort", settings.DefaultPort);
        await SetAsync("storage.dataPath", settings.DataStoragePath);
        await SetAsync("storage.autoSave", settings.AutoSaveEnabled);
        await SetAsync("chart.refreshRate", settings.ChartRefreshRate);
        await SetAsync("ui.theme", settings.ThemeName);
    }
}
