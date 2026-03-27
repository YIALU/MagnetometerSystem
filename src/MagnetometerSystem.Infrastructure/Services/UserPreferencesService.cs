using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MagnetometerSystem.Infrastructure.Database;

namespace MagnetometerSystem.Infrastructure.Services;

public class UserPreferencesService : IUserPreferencesService
{
    private readonly DatabaseInitializer _dbInitializer;

    public UserPreferencesService(DatabaseInitializer dbInitializer)
    {
        _dbInitializer = dbInitializer;
    }

    public async Task<T?> GetPreferenceAsync<T>(string key)
    {
        using var connection = new SqliteConnection(_dbInitializer.ConnectionString);
        await connection.OpenAsync();

        var sql = "SELECT value FROM user_preferences WHERE key = @Key";
        var json = await connection.QuerySingleOrDefaultAsync<string>(sql, new { Key = key });

        return json == null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetPreferenceAsync<T>(string key, T value)
    {
        using var connection = new SqliteConnection(_dbInitializer.ConnectionString);
        await connection.OpenAsync();

        var json = JsonSerializer.Serialize(value);
        var sql = @"
            INSERT INTO user_preferences (key, value, updated_at)
            VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(key) DO UPDATE SET value = @Value, updated_at = @UpdatedAt";

        await connection.ExecuteAsync(sql, new
        {
            Key = key,
            Value = json,
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
    }
}
