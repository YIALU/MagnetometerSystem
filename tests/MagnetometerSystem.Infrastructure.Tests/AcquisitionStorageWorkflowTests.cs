using System.Globalization;
using System.Text;
using Dapper;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

/// <summary>真实解析器、总线、处理、SQLite 和 CSV 的组件集成；不替代 TCP/串口及 WPF 操作验收。</summary>
public sealed class AcquisitionStorageWorkflowTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "magnetometer-flow-" + Guid.NewGuid().ToString("N"));
    private DatabaseInitializer _database = null!;
    private SqliteStorageService _storage = null!;
    private readonly DataBus _bus = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new DatabaseInitializer(Path.Combine(_directory, "acquisition.db"));
        await _database.InitializeAsync();
        _storage = new SqliteStorageService(_database, _bus);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private static SensorConfig Config(int channels, double sampleRate = 1234.5) => new()
    {
        Type = SensorType.Generic, SampleRate = sampleRate, ChannelCountOverride = channels,
        ChannelNamesOverride = Enumerable.Range(0, channels).Select(i => i == 3 ? "Temperature" : $"CH{i}").ToArray(),
        ChannelUnitsOverride = Enumerable.Range(0, channels).Select(i => i == 3 ? "°C" : "nT").ToArray()
    };

    [Theory]
    [InlineData(4, 0.025)]
    [InlineData(13, 1234.5)]
    public async Task FragmentedFrames_KeepRawAndAuxiliaryChannels_ThroughCorrectionAndCsv(int channelCount, double sampleRate)
    {
        var config = Config(channelCount, sampleRate);
        var sessionId = await _storage.StartSessionAsync("业务采集", config, new ConnectionConfig { Type = ConnectionType.Tcp });
        var protocol = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii, AsciiDelimiter = ",",
            FieldMappings = Enumerable.Range(0, channelCount).Select(i => new FieldMapping
            {
                Name = config.ChannelNames[i], Unit = config.ChannelUnits[i], ChannelIndex = i, ByteOffset = i
            }).ToList()
        };
        var parser = ParserFactory.Create(protocol);
        var collected = new List<MagnetometerReading>();
        _bus.ReadingReceived += reading => { reading.ChannelValues[0] = -999; throw new InvalidOperationException("display failed"); };
        _bus.ReadingReceived += reading => { reading.SessionId = sessionId; collected.Add(reading); };
        var frames = Enumerable.Range(0, 9).Select(row => string.Join(",",
            Enumerable.Range(0, channelCount).Select(ch => (row * 100 + ch + .125).ToString(CultureInfo.InvariantCulture))));
        var bytes = Encoding.ASCII.GetBytes("bad frame\n" + string.Join("\n", frames) + "\n");
        var chunkSizes = new[] { 1, 2, 17, 3, 81, 7 };
        var offset = 0;
        var chunk = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(chunkSizes[chunk++ % chunkSizes.Length], bytes.Length - offset);
            parser.Feed(bytes, offset, count);
            offset += count;
            while (parser.TryParse(out var reading)) _bus.PublishReading(reading!);
        }
        Assert.Equal(9, collected.Count);
        var write = _storage.SaveReadingsAsync(collected);
        collected[0].ChannelValues[0] = -123; // 入队后调用方也不能改写待保存快照。
        await _storage.EndSessionAsync(sessionId); // 未满500条的尾批必须先提交。
        await write;
        var stored = await _storage.GetReadingsAsync(sessionId);
        Assert.Equal(9, stored.Count);
        Assert.Equal(.125, stored[0].ChannelValues[0]);
        Assert.All(stored, r => Assert.Equal(channelCount, r.ChannelValues.Length));
        Assert.All(stored, r => Assert.Equal(SensorType.Generic, r.SensorType));
        var session = Assert.Single(await _storage.GetSessionsAsync());
        Assert.Equal(sampleRate, session.SampleRate);
        Assert.Equal(config.ChannelUnits, session.ChannelUnits);
        Assert.Equal(9, session.TotalReadings);
        Assert.NotNull(session.EndedAt);

        var source = stored.Select(r => r.ChannelValues[0]).ToArray();
        var filtered = new DataProcessor().MedianFilter(source, 3);
        filtered[0] = -567;
        Assert.Equal(.125, source[0]);
        var parameters = new OrthogonalityParams { Id = "profile", Offset = [1, 2, 3] };
        var corrected = await new OrthogonalityCorrector().ApplyBatchAsync(parameters, null, stored, [0, 1, 2]);
        await _storage.SaveCorrectedReadingsAsync(stored.Select((r, i) =>
            CorrectedReading.FromOriginal(r, corrected.CorrectedReadings[i].ChannelValues, parameters.Id)));
        var savedCorrection = await _storage.GetCorrectedReadingsAsync(sessionId, parameters.Id);
        Assert.Equal(-.875, savedCorrection[0].CorrectedValues[0]);
        Assert.Equal(3.125, savedCorrection[0].CorrectedValues[3]); // 温度不参与正交度。
        Assert.Null(savedCorrection[0].CorrectedTotalField); // 任意协议不猜测前三通道代表总场。
        Assert.Equal(.125, (await _storage.GetReadingsAsync(sessionId))[0].ChannelValues[0]);

        var path = Path.Combine(_directory, "result.csv");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            await new CsvExporter(_storage).ExportAsync(sessionId, path, new ExportOptions
            {
                Source = ExportDataSource.RawAndCorrected, CorrectionProfileId = parameters.Id, IncludeUnits = true
            });
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(10, lines.Length);
        Assert.Contains("Temperature_raw [°C],Temperature_corrected [°C]", lines[0]);
        var fields = lines[1].Split(',');
        Assert.Equal(1 + channelCount * 2, fields.Length);
        Assert.Equal("0.125", fields[1]);
        Assert.Equal("-0.875", fields[2]);
        Assert.Equal("3.125", fields[7]);
        Assert.Equal("3.125", fields[8]);
    }

    [Fact]
    public async Task FailedTransaction_RemainsPending_EndsOnlyAfterExplicitSuccessfulRetry()
    {
        var sessionId = await _storage.StartSessionAsync("failure", Config(4), new ConnectionConfig());
        using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("CREATE TRIGGER fail_write BEFORE INSERT ON readings BEGIN SELECT RAISE(FAIL, 'simulated disk failure'); END;");
        var reading = new MagnetometerReading { SessionId = sessionId, Timestamp = DateTime.UtcNow, ChannelValues = [1, 2, 3, 24] };
        await Assert.ThrowsAsync<SqliteException>(() => _storage.SaveReadingsAsync([reading]));
        Assert.Equal(1, _storage.WriteStatus.PendingReadings);
        Assert.NotNull(_storage.WriteStatus.LastError);
        await Assert.ThrowsAsync<IOException>(() => _storage.EndSessionAsync(sessionId));
        Assert.Null(Assert.Single(await _storage.GetSessionsAsync()).EndedAt);
        await conn.ExecuteAsync("DROP TRIGGER fail_write;");
        await _storage.RetryPendingWritesAsync();
        await _storage.EndSessionAsync(sessionId);
        Assert.Equal(0, _storage.WriteStatus.PendingReadings);
        Assert.Null(_storage.WriteStatus.LastError);
        Assert.Equal([1d, 2d, 3d, 24d], Assert.Single(await _storage.GetReadingsAsync(sessionId)).ChannelValues);
    }

    [Fact]
    public async Task CancellationDuringExport_PreservesExistingDestination_AndRemovesTemporaryFile()
    {
        var sessionId = await _storage.StartSessionAsync("cancel", Config(4), new ConnectionConfig());
        await _storage.SaveReadingsAsync(Enumerable.Range(0, 2001).Select(i => new MagnetometerReading
        { SessionId = sessionId, Timestamp = DateTime.UtcNow.AddMilliseconds(i), ChannelValues = [i, 2, 3, 24] }));
        var path = Path.Combine(_directory, "existing.csv");
        await File.WriteAllTextAsync(path, "existing content");
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CsvExporter(_storage).ExportAsync(
            sessionId, path, new ExportOptions(), new InlineProgress(_ => cts.Cancel()), cts.Token));
        Assert.Equal("existing content", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task DuplicateChannelNamesAndUtcFilters_DoNotLoseValues()
    {
        var config = Config(4);
        config.ChannelNamesOverride = ["B", "B", "Z", "Temp,room"];
        var sessionId = await _storage.StartSessionAsync("duplicate names", config, new ConnectionConfig());
        var time = new DateTime(2026, 10, 4, 1, 2, 3, DateTimeKind.Utc);
        await _storage.SaveReadingsAsync([new MagnetometerReading
        { SessionId = sessionId, Timestamp = time.ToLocalTime(), ChannelValues = [11, 22, 33, 24] }]);
        var reading = Assert.Single(await _storage.GetReadingsAsync(sessionId, time, time));
        Assert.Equal([11d, 22d, 33d, 24d], reading.ChannelValues);
        var path = Path.Combine(_directory, "quoted.csv");
        await new CsvExporter(_storage).ExportAsync(sessionId, path, new ExportOptions { IncludeUnits = true });
        Assert.Contains("\"Temp,room [°C]\"", (await File.ReadAllLinesAsync(path))[0]);
    }

    [Fact]
    public async Task LegacySchema_IsPreservedAndClearlyMarked_InsteadOfDroppingMeasurements()
    {
        var sessionId = await _storage.StartSessionAsync("old recording", Config(3), new ConnectionConfig());
        using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("DROP TABLE readings; CREATE TABLE readings(id INTEGER PRIMARY KEY, session_id TEXT, timestamp TEXT, x REAL, y REAL, z REAL);");
        await conn.ExecuteAsync("INSERT INTO readings VALUES(1,@SessionId,'2026-10-01T00:00:00Z',1,2,3); UPDATE sessions SET total_readings=1;", new { SessionId = sessionId });
        await _database.InitializeAsync();
        var session = Assert.Single(await _storage.GetSessionsAsync());
        Assert.NotNull(_database.LegacyDataWarning);
        Assert.StartsWith("readings_legacy_", session.LegacyDataTable);
        Assert.Equal(1, session.TotalReadings);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {session.LegacyDataTable}"));
        await Assert.ThrowsAsync<NotSupportedException>(() => _storage.GetReadingsAsync(sessionId));
        await Assert.ThrowsAsync<NotSupportedException>(() => _storage.DeleteSessionAsync(sessionId));
    }

    private sealed class InlineProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }
}
