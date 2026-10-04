using System.Globalization;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

public sealed class CorrectionVersionPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "correction-versions-" + Guid.NewGuid().ToString("N"));
    private SqliteStorageService _storage = null!;
    private readonly OrthogonalityCorrector _corrector = new();
    private readonly double[] _raw = [100, 200, 300, 400, 500, 600];
    private string _sessionId = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = new DatabaseInitializer(Path.Combine(_directory, "readings.db"));
        await database.InitializeAsync();
        _storage = new SqliteStorageService(database, new DataBus());
        _sessionId = await _storage.StartSessionAsync("版本隔离", new SensorConfig
        {
            Type = SensorType.Generic, ChannelCountOverride = 6,
            ChannelNamesOverride = ["X1", "Y1", "Z1", "X2", "Y2", "Z2"],
            ChannelUnitsOverride = ["nT", "nT", "nT", "nT", "nT", "nT"]
        }, new ConnectionConfig());
        await _storage.SaveReadingsAsync([new MagnetometerReading
        {
            SessionId = _sessionId, Timestamp = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc),
            ChannelValues = (double[])_raw.Clone()
        }]);
        await _storage.EndSessionAsync(_sessionId);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private static OrthogonalityParams First(string id = "first") => new() { Id = id, Offset = [1, 2, 3] };
    private static OrthogonalityParams Second(string id = "second") => new() { Id = id, Offset = [10, 20, 30] };
    private static OrthogonalityCorrectionSnapshot Snapshot(OrthogonalityParams? first = null,
        OrthogonalityParams? second = null, int[]? firstChannels = null, int[]? secondChannels = null) =>
        new(first ?? First(), second ?? Second(), firstChannels ?? [0, 1, 2], secondChannels ?? [3, 4, 5]);

    [Fact]
    public async Task DifferentProfilesMappingsAndSameIdParameterEdits_KeepEveryVersionAndExportIdentity()
    {
        var firstOffset = First(); firstOffset.Offset[0] = 4;
        var firstMatrix = First(); firstMatrix.CompensationMatrix[0] = 2;
        var secondOffset = Second(); secondOffset.Offset[1] = 40;
        var secondMatrix = Second(); secondMatrix.CompensationMatrix[4] = 2;
        var versions = new[]
        {
            Snapshot(), Snapshot(first: First("other-first")), Snapshot(second: Second("other-second")),
            Snapshot(firstChannels: [1, 0, 2]), Snapshot(secondChannels: [4, 3, 5]),
            Snapshot(first: firstOffset), Snapshot(first: firstMatrix),
            Snapshot(second: secondOffset), Snapshot(second: secondMatrix),
            new OrthogonalityCorrectionSnapshot(First(), null, [0, 1, 2])
        };
        Assert.Equal(versions.Length, versions.Select(v => v.VersionId).Distinct().Count());
        var rawBefore = await ExportAsync(ExportDataSource.Raw, null);
        var readings = await _storage.GetReadingsAsync(_sessionId);
        foreach (var snapshot in versions)
        {
            var corrected = await snapshot.ApplyBatchAsync(_corrector, readings);
            await _storage.SaveCorrectedReadingsAsync(corrected);
            await _storage.SaveCorrectedReadingsAsync(corrected); // Identical version retry remains idempotent.
            var saved = Assert.Single(await _storage.GetCorrectedReadingsAsync(_sessionId, snapshot.VersionId));
            Assert.Equal(snapshot.VersionId, saved.CorrectionProfileId);
            Assert.Equal(corrected[0].CorrectedValues, saved.CorrectedValues);
            var csv = await ExportAsync(ExportDataSource.Corrected, snapshot.VersionId);
            Assert.EndsWith(",CorrectionVersion", csv[0]);
            Assert.EndsWith("," + string.Join(",", saved.CorrectedValues.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))
                + "," + Escape(snapshot.VersionId), csv[1]);
        }
        Assert.Equal(versions.Length, (await _storage.GetCorrectedReadingsAsync(_sessionId)).Count);
        Assert.Equal(versions.Select(v => v.VersionId).OrderBy(id => id, StringComparer.Ordinal),
            (await _storage.GetCorrectionVersionIdsAsync(_sessionId)).OrderBy(id => id, StringComparer.Ordinal));
        var otherSession = await _storage.StartSessionAsync("未改正的会话", new SensorConfig
        {
            ChannelCountOverride = 1, ChannelNamesOverride = ["B"], ChannelUnitsOverride = ["nT"]
        }, new ConnectionConfig());
        await _storage.EndSessionAsync(otherSession);
        Assert.Empty(await _storage.GetCorrectionVersionIdsAsync(otherSession));
        Assert.Equal(_raw, readings[0].ChannelValues);
        Assert.Equal(_raw, Assert.Single(await _storage.GetReadingsAsync(_sessionId)).ChannelValues);
        Assert.Equal(rawBefore, await ExportAsync(ExportDataSource.Raw, null));
        await Assert.ThrowsAsync<ArgumentException>(() => ExportAsync(ExportDataSource.Corrected, null));
        Assert.Contains("first[0,1,2]|second[3,4,5]", versions[0].VersionId);
        Assert.Contains("第一组 first[0,1,2] + 第二组 second[3,4,5]", OrthogonalityCorrectionSnapshot.DisplayName(versions[0].VersionId));
    }

    [Fact]
    public async Task SnapshotFreezesProfilesAndMappingsBeforeAsyncCalculation_AndKeyIsCultureIndependent()
    {
        var first = First(); var second = Second();
        int[] firstChannels = [0, 1, 2]; int[] secondChannels = [3, 4, 5];
        var snapshot = new OrthogonalityCorrectionSnapshot(first, second, firstChannels, secondChannels);
        var beforeCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var same = First(); same.Name = "重命名"; same.Notes = "说明更改不改变计算";
            Assert.Equal(snapshot.VersionId, Snapshot(first: same).VersionId);
        }
        finally { CultureInfo.CurrentCulture = beforeCulture; }
        first.Id = "different"; first.Offset[0] = 99; first.CompensationMatrix[0] = 7;
        second.Id = "different-second"; second.Offset[0] = 90; second.CompensationMatrix[0] = 8;
        firstChannels[0] = 5; secondChannels[0] = 0;
        var result = await snapshot.ApplyBatchAsync(_corrector, await _storage.GetReadingsAsync(_sessionId));
        await _storage.SaveCorrectedReadingsAsync(result);
        var saved = Assert.Single(await _storage.GetCorrectedReadingsAsync(_sessionId, snapshot.VersionId));
        Assert.Equal(new double[] { 99, 198, 297, 390, 480, 570 }, saved.CorrectedValues);
        Assert.EndsWith("," + Escape(snapshot.VersionId), (await ExportAsync(ExportDataSource.Corrected, snapshot.VersionId))[1]);
    }

    [Fact]
    public async Task LegacySingleProfileId_RemainsSelectableAndExportableAlongsideNewVersion()
    {
        const string legacyId = "legacy \"A\",x";
        var readings = await _storage.GetReadingsAsync(_sessionId);
        var legacy = CorrectedReading.FromOriginal(readings[0], [99, 198, 297, 400, 500, 600], legacyId);
        await _storage.SaveCorrectedReadingsAsync([legacy]);
        var snapshot = new OrthogonalityCorrectionSnapshot(First(legacyId), null, [0, 1, 2]);
        await _storage.SaveCorrectedReadingsAsync(await snapshot.ApplyBatchAsync(_corrector, readings));
        Assert.Equal(2, (await _storage.GetCorrectedReadingsAsync(_sessionId)).Count);
        Assert.Contains(legacyId, await _storage.GetCorrectionVersionIdsAsync(_sessionId));
        Assert.Contains(snapshot.VersionId, await _storage.GetCorrectionVersionIdsAsync(_sessionId));
        Assert.Equal(legacyId, Assert.Single(await _storage.GetCorrectedReadingsAsync(_sessionId, legacyId)).CorrectionProfileId);
        Assert.Equal($"旧配置：{legacyId}", OrthogonalityCorrectionSnapshot.DisplayName(legacyId));
        foreach (var source in new[] { ExportDataSource.Corrected, ExportDataSource.RawAndCorrected })
        {
            var legacyCsv = await ExportAsync(source, legacyId);
            Assert.EndsWith(",CorrectionVersion", legacyCsv[0]);
            Assert.EndsWith("," + Escape(legacyId), legacyCsv[1]);
            Assert.EndsWith("," + Escape(snapshot.VersionId), (await ExportAsync(source, snapshot.VersionId))[1]);
        }
        Assert.Equal(_raw, Assert.Single(await _storage.GetReadingsAsync(_sessionId)).ChannelValues);
    }

    private async Task<string[]> ExportAsync(ExportDataSource source, string? version)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".csv");
        await new CsvExporter(_storage).ExportAsync(_sessionId, path, new ExportOptions
        {
            Source = source, CorrectionProfileId = version
        });
        return await File.ReadAllLinesAsync(path);
    }

    private static string Escape(string value) => value.IndexOfAny([',', '\"', '\r', '\n']) < 0
        ? value : '\"' + value.Replace("\"", "\"\"") + '\"';
}
