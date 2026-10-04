using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.Infrastructure.Tests;

public class CsvExporterTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _csvPath;
    private DatabaseInitializer _dbInit = null!;
    private SqliteStorageService _storageService = null!;
    private CsvExporter _exporter = null!;
    private DataBus _dataBus;

    public CsvExporterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_csv_{Guid.NewGuid():N}.db");
        _csvPath = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.csv");
        _dataBus = new DataBus();
    }

    public async Task InitializeAsync()
    {
        _dbInit = new DatabaseInitializer(_dbPath);
        await _dbInit.InitializeAsync();
        _storageService = new SqliteStorageService(_dbInit, _dataBus);
        _exporter = new CsvExporter(_storageService);
    }

    public Task DisposeAsync()
    {
        _storageService?.Dispose();
        Thread.Sleep(100);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
            if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
            if (File.Exists(_csvPath)) File.Delete(_csvPath);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static SensorConfig CreateDefaultConfig() => new()
    {
        Type = SensorType.TriaxialFluxgate,
        SampleRate = 100.0
    };

    private async Task<string> CreateSessionWithReadings(int readingCount, DateTime? baseTime = null)
    {
        var config = CreateDefaultConfig();
        var sessionId = await _storageService.StartSessionAsync("Export Test", config, new ConnectionConfig());
        var bt = baseTime ?? new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var readings = Enumerable.Range(0, readingCount).Select(i =>
            new MagnetometerReading
            {
                SessionId = sessionId,
                Timestamp = bt.AddSeconds(i),
                SensorType = SensorType.TriaxialFluxgate,
                ChannelValues = [i * 1.0, i * 2.0, i * 3.0],
                IsCalibrated = false,
                IsOrthogonalityCorrected = false
            }).ToArray();

        if (readings.Length > 0)
        {
            await _storageService.SaveReadingsAsync(readings);
            await _storageService.WaitForPendingWritesAsync();
        }

        return sessionId;
    }

    [Fact]
    public async Task ExportAsync_WritesHeaderAndData()
    {
        // Arrange
        var sessionId = await CreateSessionWithReadings(3);
        var options = new ExportOptions { IncludeHeader = true };

        // Act
        await _exporter.ExportAsync(sessionId, _csvPath, options);

        // Assert
        Assert.True(File.Exists(_csvPath));
        var lines = await File.ReadAllLinesAsync(_csvPath);
        Assert.True(lines.Length >= 4, $"Expected at least 4 lines (1 header + 3 data), got {lines.Length}");

        // Check header
        Assert.Contains("Timestamp", lines[0]);
        Assert.Contains("X", lines[0]);
        Assert.Contains("Y", lines[0]);
        Assert.Contains("Z", lines[0]);
    }

    [Fact]
    public async Task ExportAsync_FiltersChannels()
    {
        // Arrange
        var sessionId = await CreateSessionWithReadings(3);
        var options = new ExportOptions
        {
            IncludeHeader = true,
            ChannelIndices = [0, 2] // X and Z only
        };

        // Act
        await _exporter.ExportAsync(sessionId, _csvPath, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(_csvPath);
        var header = lines[0];
        var headerParts = header.Split(',');
        // Timestamp, X, Z = 3 columns
        Assert.Equal(3, headerParts.Length);
        Assert.Equal("Timestamp", headerParts[0]);
        Assert.Equal("X", headerParts[1]);
        Assert.Equal("Z", headerParts[2]);
    }

    [Fact]
    public async Task ExportAsync_FiltersTimeRange()
    {
        // Arrange
        var baseTime = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var sessionId = await CreateSessionWithReadings(10, baseTime);
        var options = new ExportOptions
        {
            IncludeHeader = true,
            StartTime = baseTime.AddSeconds(3),
            EndTime = baseTime.AddSeconds(6)
        };

        // Act
        await _exporter.ExportAsync(sessionId, _csvPath, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(_csvPath);
        // Header + 4 data rows (seconds 3, 4, 5, 6)
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task ExportAsync_ReportsProgress()
    {
        // The synchronous observer makes progress assertions independent of dispatcher scheduling.
        var sessionId = await CreateSessionWithReadings(5);
        var options = new ExportOptions { IncludeHeader = true };
        var progressValues = new List<double>();
        var progress = new InlineProgress<double>(progressValues.Add);

        // Act
        await _exporter.ExportAsync(sessionId, _csvPath, options, progress);

        // Assert - at minimum, final progress of 1.0 should be reported
        Assert.Contains(progressValues, v => Math.Abs(v - 1.0) < 0.001);
    }

    [Fact]
    public async Task ExportAsync_EmptySession_WritesHeaderOnly()
    {
        // Arrange
        var config = CreateDefaultConfig();
        var sessionId = await _storageService.StartSessionAsync("Empty Session", config, new ConnectionConfig());
        var options = new ExportOptions { IncludeHeader = true };

        // Act
        await _exporter.ExportAsync(sessionId, _csvPath, options);

        // Assert
        Assert.True(File.Exists(_csvPath));
        var lines = await File.ReadAllLinesAsync(_csvPath);
        // Should have just the header line (plus possibly empty trailing line)
        var nonEmptyLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Single(nonEmptyLines);
        Assert.Contains("Timestamp", nonEmptyLines[0]);
    }

    [Theory]
    [InlineData(2000, 0.5)]
    [InlineData(1000, 1.0)]
    public async Task ExportAsync_CancellationDeletesFile(int readingCount, double cancellationProgress)
    {
        var sessionId = await CreateSessionWithReadings(readingCount);
        using var cts = new CancellationTokenSource();
        var progressValues = new List<double>();
        var progress = new InlineProgress<double>(value =>
        {
            progressValues.Add(value);
            // The exporter calls IProgress synchronously after writing rows. Cancel here rather
            // than racing a timer against machine speed. The output must already exist on disk.
            Assert.True(File.Exists(_csvPath));
            Assert.True(new FileInfo(_csvPath).Length > 0);
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _exporter.ExportAsync(sessionId, _csvPath, new ExportOptions(), progress, cts.Token));

        Assert.Equal(cancellationProgress, Assert.Single(progressValues));
        Assert.True(cts.IsCancellationRequested);
        Assert.False(File.Exists(_csvPath));
        // Cancellation deletes only the incomplete export, leaving the stored session intact.
        Assert.Equal(readingCount, (await _storageService.GetReadingsAsync(sessionId)).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task ExportAsync_AlreadyCanceledPreservesExistingDestination(int readingCount)
    {
        var sessionId = await CreateSessionWithReadings(readingCount);
        const string existing = "keep previous export";
        await File.WriteAllTextAsync(_csvPath, existing);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _exporter.ExportAsync(sessionId, _csvPath, new ExportOptions(), ct: cts.Token));

        Assert.Equal(existing, await File.ReadAllTextAsync(_csvPath));
    }

    [Fact]
    public async Task ExportAsync_EmptySessionCancellationDeletesHeaderFile()
    {
        var sessionId = await CreateSessionWithReadings(0);
        using var cts = new CancellationTokenSource();
        bool outputCreated = false;
        var progress = new InlineProgress<double>(value =>
        {
            Assert.Equal(1.0, value);
            outputCreated = File.Exists(_csvPath) && new FileInfo(_csvPath).Length > 0;
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _exporter.ExportAsync(sessionId, _csvPath, new ExportOptions { IncludeHeader = true }, progress, cts.Token));

        Assert.True(outputCreated);
        Assert.False(File.Exists(_csvPath));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
