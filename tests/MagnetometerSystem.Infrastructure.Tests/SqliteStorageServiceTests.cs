using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;

namespace MagnetometerSystem.Infrastructure.Tests;

public class SqliteStorageServiceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private DatabaseInitializer _dbInit = null!;
    private SqliteStorageService _service = null!;
    private DataBus _dataBus;

    public SqliteStorageServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _dataBus = new DataBus();
    }

    public async Task InitializeAsync()
    {
        _dbInit = new DatabaseInitializer(_dbPath);
        await _dbInit.InitializeAsync();
        _service = new SqliteStorageService(_dbInit, _dataBus);
    }

    public Task DisposeAsync()
    {
        _service?.Dispose();
        // Small delay to let SQLite release file handles
        Thread.Sleep(100);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            // Also clean WAL/SHM files
            var walPath = _dbPath + "-wal";
            var shmPath = _dbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch
        {
            // Best-effort cleanup
        }
        return Task.CompletedTask;
    }

    private static SensorConfig CreateDefaultConfig() => new()
    {
        Type = SensorType.TriaxialFluxgate,
        SampleRate = 100.0
    };

    private static ConnectionConfig CreateDefaultConnectionConfig() => new();

    private static MagnetometerReading CreateReading(string sessionId, DateTime timestamp, double[] values) => new()
    {
        SessionId = sessionId,
        Timestamp = timestamp,
        SensorType = SensorType.TriaxialFluxgate,
        ChannelValues = values,
        IsCalibrated = false,
        IsOrthogonalityCorrected = false
    };

    [Fact]
    public async Task StartSessionAsync_CreatesSession_ReturnsId()
    {
        // Act
        var sessionId = await _service.StartSessionAsync("Test Session", CreateDefaultConfig(), CreateDefaultConnectionConfig());

        // Assert
        Assert.NotNull(sessionId);
        Assert.NotEmpty(sessionId);
        Assert.True(Guid.TryParse(sessionId, out _), "Session ID should be a valid GUID");
    }

    [Fact]
    public async Task EndSessionAsync_UpdatesEndedAt()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Test Session", CreateDefaultConfig(), CreateDefaultConnectionConfig());

        // Act
        await _service.EndSessionAsync(sessionId);

        // Assert
        var sessions = await _service.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        Assert.NotNull(session);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public async Task SaveReadingsAsync_PersistsReadings()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Test Session", CreateDefaultConfig(), CreateDefaultConnectionConfig());
        var baseTime = DateTime.UtcNow;
        var readings = new[]
        {
            CreateReading(sessionId, baseTime, [1.0, 2.0, 3.0]),
            CreateReading(sessionId, baseTime.AddSeconds(1), [4.0, 5.0, 6.0]),
            CreateReading(sessionId, baseTime.AddSeconds(2), [7.0, 8.0, 9.0])
        };

        // Act
        await _service.SaveReadingsAsync(readings);
        // Wait for background consumer to flush
        await Task.Delay(1000);

        // Assert
        var retrieved = await _service.GetReadingsAsync(sessionId);
        Assert.Equal(3, retrieved.Count);
        Assert.Equal(1.0, retrieved[0].ChannelValues[0]);
        Assert.Equal(5.0, retrieved[1].ChannelValues[1]);
        Assert.Equal(9.0, retrieved[2].ChannelValues[2]);
    }

    [Fact]
    public async Task GetReadingsAsync_FiltersByTimeRange()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Test Session", CreateDefaultConfig(), CreateDefaultConnectionConfig());
        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var readings = new[]
        {
            CreateReading(sessionId, baseTime, [1.0, 2.0, 3.0]),
            CreateReading(sessionId, baseTime.AddMinutes(1), [4.0, 5.0, 6.0]),
            CreateReading(sessionId, baseTime.AddMinutes(2), [7.0, 8.0, 9.0]),
            CreateReading(sessionId, baseTime.AddMinutes(3), [10.0, 11.0, 12.0])
        };

        await _service.SaveReadingsAsync(readings);
        await Task.Delay(1000);

        // Act - filter middle range
        var filtered = await _service.GetReadingsAsync(
            sessionId,
            baseTime.AddSeconds(30),
            baseTime.AddMinutes(2).AddSeconds(30));

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.Equal(4.0, filtered[0].ChannelValues[0]);
        Assert.Equal(7.0, filtered[1].ChannelValues[0]);
    }

    [Fact]
    public async Task GetSessionsAsync_ReturnsAllSessions()
    {
        // Arrange
        var config = CreateDefaultConfig();
        var connConfig = CreateDefaultConnectionConfig();
        await _service.StartSessionAsync("Session 1", config, connConfig);
        await _service.StartSessionAsync("Session 2", config, connConfig);
        await _service.StartSessionAsync("Session 3", config, connConfig);

        // Act
        var sessions = await _service.GetSessionsAsync();

        // Assert
        Assert.Equal(3, sessions.Count);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionAndReadings()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Test Session", CreateDefaultConfig(), CreateDefaultConnectionConfig());
        var baseTime = DateTime.UtcNow;
        var readings = new[]
        {
            CreateReading(sessionId, baseTime, [1.0, 2.0, 3.0]),
            CreateReading(sessionId, baseTime.AddSeconds(1), [4.0, 5.0, 6.0])
        };
        await _service.SaveReadingsAsync(readings);
        await Task.Delay(1000);

        // Act
        await _service.DeleteSessionAsync(sessionId);

        // Assert
        var sessions = await _service.GetSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.Id == sessionId);

        var remainingReadings = await _service.GetReadingsAsync(sessionId);
        Assert.Empty(remainingReadings);
    }

    [Fact]
    public async Task UpdateSessionAsync_UpdatesNameAndNotes()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Original Name", CreateDefaultConfig(), CreateDefaultConnectionConfig());

        // Act
        await _service.UpdateSessionAsync(sessionId, "Updated Name", "Some notes here");

        // Assert
        var sessions = await _service.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        Assert.NotNull(session);
        Assert.Equal("Updated Name", session.Name);
        Assert.Equal("Some notes here", session.Notes);
    }

    [Fact]
    public async Task StartSessionAsync_StoresSensorConfig()
    {
        // Arrange
        var config = new SensorConfig
        {
            Type = SensorType.DualTriaxialFluxgate,
            SampleRate = 200.0
        };

        // Act
        var sessionId = await _service.StartSessionAsync("Config Test", config, CreateDefaultConnectionConfig());

        // Assert
        var sessions = await _service.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        Assert.NotNull(session);
        Assert.Equal(SensorType.DualTriaxialFluxgate, session.SensorType);
        Assert.Equal(200.0, session.SampleRate);
        Assert.Equal(6, session.ChannelCount);
    }

    [Fact]
    public async Task SaveReadingsAsync_HandlesLargeChannelValues()
    {
        // Arrange - test with 6 channels (dual triaxial)
        var config = new SensorConfig { Type = SensorType.DualTriaxialFluxgate, SampleRate = 100.0 };
        var sessionId = await _service.StartSessionAsync("Multi-channel", config, CreateDefaultConnectionConfig());
        var baseTime = DateTime.UtcNow;
        var readings = new[]
        {
            new MagnetometerReading
            {
                SessionId = sessionId,
                Timestamp = baseTime,
                SensorType = SensorType.DualTriaxialFluxgate,
                ChannelValues = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0],
                IsCalibrated = true,
                IsOrthogonalityCorrected = true
            }
        };

        // Act
        await _service.SaveReadingsAsync(readings);
        await Task.Delay(1000);

        // Assert
        var retrieved = await _service.GetReadingsAsync(sessionId);
        Assert.Single(retrieved);
        Assert.Equal(6, retrieved[0].ChannelValues.Length);
        Assert.Equal(4.0, retrieved[0].ChannelValues[3]);
        Assert.True(retrieved[0].IsCalibrated);
        Assert.True(retrieved[0].IsOrthogonalityCorrected);
    }

    [Fact]
    public async Task EndSessionAsync_CountsReadings()
    {
        // Arrange
        var sessionId = await _service.StartSessionAsync("Count Test", CreateDefaultConfig(), CreateDefaultConnectionConfig());
        var baseTime = DateTime.UtcNow;
        var readings = Enumerable.Range(0, 5).Select(i =>
            CreateReading(sessionId, baseTime.AddSeconds(i), [i * 1.0, i * 2.0, i * 3.0])).ToArray();
        await _service.SaveReadingsAsync(readings);
        await Task.Delay(1000);

        // Act
        await _service.EndSessionAsync(sessionId);

        // Assert
        var sessions = await _service.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        Assert.NotNull(session);
        Assert.Equal(5, session.TotalReadings);
    }
}
