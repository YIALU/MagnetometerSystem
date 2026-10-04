using System.Text.Json;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

public sealed class OrthogonalityUnitPersistenceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orthogonality-unit-{Guid.NewGuid():N}.db");
    private DatabaseInitializer _database = null!;
    private SqliteCalibrationRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _database = new DatabaseInitializer(_path);
        await _database.InitializeAsync();
        _repository = new SqliteCalibrationRepository(_database);
    }

    public Task DisposeAsync()
    {
        using (var connection = new SqliteConnection(_database.ConnectionString))
        {
            connection.Open();
            SqliteConnection.ClearPool(connection);
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("nT")]
    [InlineData("uT")]
    [InlineData("mT")]
    [InlineData("T")]
    public async Task SavedProfile_RetainsOffsetUnitThroughBothQueriesAndJsonExport(string unit)
    {
        var profile = new OrthogonalityParams { Id = "same-profile", Unit = unit, Offset = [100, 200, 300] };
        await _repository.SaveOrthogonalityProfileAsync(profile);
        var loaded = Assert.IsType<OrthogonalityParams>(await _repository.GetOrthogonalityProfileAsync(profile.Id));
        var listed = Assert.Single(await _repository.GetOrthogonalityProfilesAsync());
        Assert.Equal(unit, loaded.Unit);
        Assert.Equal(unit, listed.Unit);
        Assert.Equal(profile.Offset, loaded.Offset);
        loaded.ValidateUnit(unit);

        string export = Path.ChangeExtension(_path, ".json");
        try
        {
            await File.WriteAllTextAsync(export, JsonSerializer.Serialize(loaded));
            var imported = JsonSerializer.Deserialize<OrthogonalityParams>(await File.ReadAllTextAsync(export))!;
            Assert.Equal(unit, imported.Unit);
            imported.ValidateUnit(unit);
            Assert.Throws<ArgumentException>(() => imported.ValidateUnit(unit == "nT" ? "uT" : "nT"));
        }
        finally { File.Delete(export); }
    }

    [Fact]
    public async Task ExistingDatabaseWithoutUnitColumn_MigratesLegacyProfileAsUnknownWithoutRelabelingOffsets()
    {
        var profile = new OrthogonalityParams { Id = "legacy", Unit = "nT", Offset = [100, 200, 300] };
        await _repository.SaveOrthogonalityProfileAsync(profile);
        using (var connection = new SqliteConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE orthogonality_profiles DROP COLUMN unit";
            await command.ExecuteNonQueryAsync();
        }

        await _database.InitializeAsync();
        await _database.InitializeAsync(); // Reopening an already migrated database remains idempotent.
        var legacy = Assert.IsType<OrthogonalityParams>(await _repository.GetOrthogonalityProfileAsync("legacy"));
        Assert.Equal("", legacy.Unit);
        Assert.Equal(profile.Offset, legacy.Offset);
        Assert.Throws<ArgumentException>(() => legacy.ValidateUnit("nT"));
        var exported = JsonSerializer.Deserialize<OrthogonalityParams>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal("", exported.Unit);
        await _repository.SaveOrthogonalityProfileAsync(legacy);
        Assert.Equal("", Assert.Single(await _repository.GetOrthogonalityProfilesAsync()).Unit);
    }
}
