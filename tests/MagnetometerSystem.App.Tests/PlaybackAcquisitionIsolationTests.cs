using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class PlaybackAcquisitionIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PlaybackBlocksOpeningAcquisitionUntilStoppedThenFirstConnectSavesImmediateFrame(bool paused) => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        int starts = 0;
        fixture.Bus.AcquisitionStarting += _ => { starts++; return Task.CompletedTask; };
        await fixture.History.PlayCommand.ExecuteAsync(null);
        StopPlaybackTimer(fixture.History);
        if (paused) fixture.History.PauseCommand.Execute(null);
        Assert.True(fixture.Bus.IsPlaybackMode);
        Assert.Equal(paused ? PlaybackState.Paused : PlaybackState.Playing, fixture.History.State);

        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Factory.CreateCalls);
        Assert.Equal(0, fixture.Transport.ConnectCalls);
        Assert.Equal(0, starts);
        Assert.Null(fixture.Bus.CurrentConnection);
        Assert.Null(fixture.Sessions.ActiveSessionId);
        Assert.Single(await fixture.Storage.GetSessionsAsync());
        Assert.Contains("回放", fixture.ConnectionVm.StatusMessage);
        Assert.True(fixture.Bus.IsPlaybackMode);

        fixture.History.StopCommand.Execute(null);
        Assert.False(fixture.Bus.IsPlaybackMode);
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null); // One click opens and emits its first frame inline.
        Assert.Equal(1, fixture.Factory.CreateCalls);
        Assert.Equal(1, fixture.Transport.ConnectCalls);
        Assert.Equal(1, starts);
        Assert.True(fixture.Transport.IsConnected);
        string sessionId = Assert.IsType<string>(fixture.Sessions.ActiveSessionId);
        Assert.NotEqual(fixture.HistorySessionId, sessionId);
        await fixture.ConnectionVm.StopAcquisitionAsync();
        var live = Assert.Single(await fixture.Storage.GetReadingsAsync(sessionId));
        Assert.Equal(new[] { 42d, 43d, 44d }, live.ChannelValues);
        var info = (await fixture.Storage.GetSessionsAsync()).Single(s => s.Id == sessionId);
        Assert.Equal(1, info.TotalReadings);
        Assert.NotNull(info.EndedAt);
        Assert.Equal(2, (await fixture.Storage.GetReadingsAsync(fixture.HistorySessionId)).Count);
    });

    [Fact]
    public Task ActiveConnectionBlocksPlayAndSeekIncludingDisconnectedReconnectState() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var published = new List<double>();
        fixture.Bus.ReadingReceived += reading => published.Add(reading.ChannelValues[0]);
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        string sessionId = Assert.IsType<string>(fixture.Sessions.ActiveSessionId);
        Assert.Equal(new[] { 42d }, published);

        foreach (bool physicallyConnected in new[] { true, false })
        {
            fixture.Transport.SetConnected(physicallyConnected);
            Assert.Same(fixture.Transport, fixture.Bus.CurrentConnection);
            Assert.False(fixture.History.PlayCommand.CanExecute(null));
            await fixture.History.PlayCommand.ExecuteAsync(null);
            fixture.History.SeekTo(1);
            fixture.History.OnSeekDragStarted();
            fixture.History.OnSeekDragCompleted(0.5);
            Assert.False(fixture.Bus.IsPlaybackMode);
            Assert.False(fixture.History.IsPlaying);
            Assert.Equal(PlaybackState.Ready, fixture.History.State);
            Assert.Equal(0, fixture.History.CurrentIndex);
            Assert.Equal(new[] { 42d }, published); // No historical 900/901 values entered the live stream.
            Assert.Equal(sessionId, fixture.Sessions.ActiveSessionId);
        }

        fixture.Transport.SetConnected(true);
        fixture.Transport.Feed("45,46,47\n");
        await fixture.ConnectionVm.StopAcquisitionAsync();
        Assert.Equal(new[] { 42d, 45d }, (await fixture.Storage.GetReadingsAsync(sessionId)).Select(r => r.ChannelValues[0]));
        Assert.Equal(new[] { 900d, 901d }, (await fixture.Storage.GetReadingsAsync(fixture.HistorySessionId)).Select(r => r.ChannelValues[0]));
    });

    private static void StopPlaybackTimer(HistoryPlaybackViewModel vm) =>
        ((DispatcherTimer?)typeof(HistoryPlaybackViewModel).GetField("_playbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm))?.Stop();

    private sealed class Fixture : IAsyncDisposable
    {
        public required DataBus Bus { get; init; }
        public required SqliteStorageService Storage { get; init; }
        public required SessionListViewModel Sessions { get; init; }
        public required ConnectionViewModel ConnectionVm { get; init; }
        public required HistoryPlaybackViewModel History { get; init; }
        public required TestConnection Transport { get; init; }
        public required TestConnectionFactory Factory { get; init; }
        public required string HistorySessionId { get; init; }
        public required string DatabasePath { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"playback-isolation-{Guid.NewGuid():N}.db");
            var database = new DatabaseInitializer(path);
            await database.InitializeAsync();
            var bus = new DataBus();
            var storage = new SqliteStorageService(database, bus);
            string historyId = await storage.StartSessionAsync("history", new SensorConfig
            {
                SampleRate = 1, ChannelCountOverride = 3,
                ChannelNamesOverride = ["X", "Y", "Z"], ChannelUnitsOverride = ["nT", "nT", "nT"],
            }, new());
            DateTime start = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            await storage.SaveReadingsAsync(new[] { 0, 1 }.Select(i => new MagnetometerReading
            { SessionId = historyId, Timestamp = start.AddHours(i), ChannelValues = [900 + i, 0, 0] }));
            await storage.EndSessionAsync(historyId);
            var corrector = new OrthogonalityCorrector();
            var repository = new SqliteCalibrationRepository(database);
            var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repository);
            var transport = new TestConnection();
            var factory = new TestConnectionFactory(transport);
            var connectionVm = new ConnectionViewModel(factory, bus, corrector, repository);
            var history = new HistoryPlaybackViewModel(storage, bus, corrector, repository)
            { SelectedSession = Assert.Single(await storage.GetSessionsAsync()) };
            await history.LoadSessionCommand.ExecuteAsync(null);
            return new Fixture
            {
                Bus = bus, Storage = storage, Sessions = sessions, ConnectionVm = connectionVm,
                History = history, Transport = transport, Factory = factory,
                HistorySessionId = historyId, DatabasePath = path,
            };
        }

        public async ValueTask DisposeAsync()
        {
            History.StopCommand.Execute(null);
            await ConnectionVm.StopAcquisitionAsync();
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(DatabasePath + suffix);
        }
    }

    private sealed class TestConnectionFactory(TestConnection connection) : IConnectionFactory
    {
        public int CreateCalls { get; private set; }
        public IDeviceConnection Create(ConnectionConfig config) { CreateCalls++; return connection; }
    }

    private sealed class TestConnection : IDeviceConnection
    {
        public event EventHandler<byte[]>? DataReceived;
        public event EventHandler<string>? ErrorOccurred { add { } remove { } }
        public event EventHandler<bool>? ConnectionStateChanged;
        public bool IsConnected { get; private set; }
        public int ConnectCalls { get; private set; }
        public ConnectionConfig Config { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default)
        {
            ConnectCalls++;
            SetConnected(true);
            Feed("42,43,44\n"); // Device emits immediately, before ConnectAsync returns.
            return Task.CompletedTask;
        }
        public void SetConnected(bool value) { IsConnected = value; ConnectionStateChanged?.Invoke(this, value); }
        public Task DisconnectAsync() { SetConnected(false); return Task.CompletedTask; }
        public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Feed(string text) => DataReceived?.Invoke(this, Encoding.ASCII.GetBytes(text));
    }
}
