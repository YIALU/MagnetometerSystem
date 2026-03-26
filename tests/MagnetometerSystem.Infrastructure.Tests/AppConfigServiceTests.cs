using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;

namespace MagnetometerSystem.Infrastructure.Tests;

public class AppConfigServiceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private DatabaseInitializer _dbInit = null!;
    private AppConfigService _service = null!;

    public AppConfigServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_config_{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _dbInit = new DatabaseInitializer(_dbPath);
        await _dbInit.InitializeAsync();
        _service = new AppConfigService(_dbInit);
    }

    public Task DisposeAsync()
    {
        Thread.Sleep(100);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var walPath = _dbPath + "-wal";
            var shmPath = _dbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_WhenKeyNotFound()
    {
        // Act
        var stringResult = await _service.GetAsync<string>("nonexistent.key");
        var intResult = await _service.GetAsync<int>("nonexistent.int.key");
        var boolResult = await _service.GetAsync<bool>("nonexistent.bool.key");

        // Assert
        Assert.Null(stringResult);
        Assert.Equal(default, intResult);
        Assert.Equal(default, boolResult);
    }

    [Fact]
    public async Task SetAsync_GetAsync_RoundTripsString()
    {
        // Act
        await _service.SetAsync("test.string", "hello world");
        var result = await _service.GetAsync<string>("test.string");

        // Assert
        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task SetAsync_GetAsync_RoundTripsInt()
    {
        // Act
        await _service.SetAsync("test.int", 42);
        var result = await _service.GetAsync<int>("test.int");

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task SetAsync_GetAsync_RoundTripsBool()
    {
        // Act
        await _service.SetAsync("test.bool", true);
        var result = await _service.GetAsync<bool>("test.bool");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        // Arrange
        await _service.SetAsync("test.overwrite", "original");

        // Act
        await _service.SetAsync("test.overwrite", "updated");
        var result = await _service.GetAsync<string>("test.overwrite");

        // Assert
        Assert.Equal("updated", result);
    }

    [Fact]
    public async Task LoadSettingsAsync_ReturnsDefaults_WhenEmpty()
    {
        // Act
        var settings = await _service.LoadSettingsAsync();

        // Assert
        Assert.NotNull(settings);
        Assert.Equal(115200, settings.DefaultBaudRate);
        Assert.Equal(5000, settings.DefaultPort);
        Assert.Equal("", settings.DataStoragePath);
        Assert.True(settings.AutoSaveEnabled);
        Assert.Equal(30, settings.ChartRefreshRate);
        Assert.Equal("Default", settings.ThemeName);
    }

    [Fact]
    public async Task SaveSettingsAsync_LoadSettingsAsync_RoundTrips()
    {
        // Arrange
        var settings = new AppSettings
        {
            DefaultPortName = "COM5",
            DefaultBaudRate = 9600,
            DefaultIpAddress = "10.0.0.1",
            DefaultPort = 8080,
            DataStoragePath = "/tmp/data",
            AutoSaveEnabled = false,
            ChartRefreshRate = 60,
            ThemeName = "Dark"
        };

        // Act
        await _service.SaveSettingsAsync(settings);
        var loaded = await _service.LoadSettingsAsync();

        // Assert
        Assert.Equal("COM5", loaded.DefaultPortName);
        Assert.Equal(9600, loaded.DefaultBaudRate);
        Assert.Equal("10.0.0.1", loaded.DefaultIpAddress);
        Assert.Equal(8080, loaded.DefaultPort);
        Assert.Equal("/tmp/data", loaded.DataStoragePath);
        Assert.False(loaded.AutoSaveEnabled);
        Assert.Equal(60, loaded.ChartRefreshRate);
        Assert.Equal("Dark", loaded.ThemeName);
    }

    [Fact]
    public async Task GetAsync_HandlesComplexObjects()
    {
        // Arrange
        var complexObject = new TestComplexConfig
        {
            Name = "TestSensor",
            Values = [1.1, 2.2, 3.3],
            Nested = new TestNestedConfig { Enabled = true, Threshold = 0.5 }
        };

        // Act
        await _service.SetAsync("test.complex", complexObject);
        var result = await _service.GetAsync<TestComplexConfig>("test.complex");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TestSensor", result.Name);
        Assert.Equal(3, result.Values.Length);
        Assert.Equal(2.2, result.Values[1]);
        Assert.NotNull(result.Nested);
        Assert.True(result.Nested.Enabled);
        Assert.Equal(0.5, result.Nested.Threshold);
    }

    // Helper types for complex object test
    private class TestComplexConfig
    {
        public string Name { get; set; } = "";
        public double[] Values { get; set; } = [];
        public TestNestedConfig? Nested { get; set; }
    }

    private class TestNestedConfig
    {
        public bool Enabled { get; set; }
        public double Threshold { get; set; }
    }
}
