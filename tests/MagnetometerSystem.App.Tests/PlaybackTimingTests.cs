using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class PlaybackTimingTests
{
    [Fact]
    public Task RecordedIntervalsControlEachSpeedAndCompletionExitsPlayback() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        int stops = 0;
        fixture.Bus.AcquisitionStopped += () => stops++;
        foreach (double speed in new[] { 0.5, 1.0, 5.0, 10.0 })
        {
            fixture.Displayed.Clear();
            fixture.Vm.PlaybackSpeed = speed;
            await fixture.Vm.PlayCommand.ExecuteAsync(null);
            Advance(fixture.Vm, 3.5 / speed);
            Assert.Equal(new[] { 0d }, fixture.Values);
            Advance(fixture.Vm, 4.5 / speed);
            Assert.Equal(new[] { 0d, 4d }, fixture.Values);
            Assert.Equal(PlaybackState.Playing, fixture.Vm.State);
            Assert.True(fixture.Bus.IsPlaybackMode);
            Advance(fixture.Vm, 10.5 / speed);
            Assert.Equal(new[] { 0d, 4d, 10d }, fixture.Values);
            Assert.Equal(PlaybackState.Completed, fixture.Vm.State);
            Assert.Equal(1, fixture.Vm.Progress);
            Assert.False(fixture.Vm.IsPlaying);
            Assert.False(fixture.Bus.IsPlaybackMode);
            Assert.False(Field<Stopwatch>(fixture.Vm, "_playbackClock").IsRunning);
        }
        Assert.Equal(4, stops); // Exactly one completed notification per playback, including restarts.
        Assert.Equal(1000, fixture.Vm.SelectedSession!.SampleRate);
    });

    [Fact]
    public Task PauseAndResumePreservePositionInsideARecordedGap() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 2);
        fixture.Vm.PauseCommand.Execute(null);
        Assert.True(fixture.Vm.IsPaused);
        Assert.False(Field<Stopwatch>(fixture.Vm, "_playbackClock").IsRunning);
        Assert.InRange(Field<TimeSpan>(fixture.Vm, "_positionAtTimerStart").TotalSeconds, 1.9, 2.1);
        Assert.Equal(new[] { 0d }, fixture.Values);
        Assert.True(fixture.Bus.IsPlaybackMode);

        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 1);
        Assert.Equal(new[] { 0d }, fixture.Values); // Recorded position is 3s, next reading is at 4s.
        Advance(fixture.Vm, 2.5);
        Assert.Equal(new[] { 0d, 4d }, fixture.Values);
        fixture.Vm.StopCommand.Execute(null);
        Assert.False(fixture.Bus.IsPlaybackMode);
        Assert.Equal(0, fixture.Vm.CurrentIndex);
        Assert.Equal(TimeSpan.Zero, Field<TimeSpan>(fixture.Vm, "_positionAtTimerStart"));
    });

    [Fact]
    public Task SeekingWhilePlayingOrPausedRebasesTheRecordedTimeline() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 1);
        fixture.Vm.OnSeekDragStarted();
        fixture.Vm.OnSeekDragCompleted(0.5); // Middle record is at 4s, not half the nominal-rate duration.
        Assert.Equal(PlaybackState.Playing, fixture.Vm.State);
        Advance(fixture.Vm, 0);
        Assert.Equal(new[] { 0d, 4d }, fixture.Values);
        Advance(fixture.Vm, 5.5);
        Assert.Equal(new[] { 0d, 4d }, fixture.Values); // Position 9.5s has not reached the 10s record.

        fixture.Vm.PauseCommand.Execute(null);
        fixture.Vm.OnSeekDragStarted();
        fixture.Vm.OnSeekDragCompleted(0);
        Assert.Equal(PlaybackState.Paused, fixture.Vm.State);
        int index = fixture.Vm.CurrentIndex;
        fixture.Vm.SeekTo(double.NaN);
        fixture.Vm.SeekTo(double.PositiveInfinity);
        Assert.Equal(index, fixture.Vm.CurrentIndex);
        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 0);
        Assert.Equal(new[] { 0d, 4d, 0d }, fixture.Values);
        Advance(fixture.Vm, 4.5);
        Assert.Equal(new[] { 0d, 4d, 0d, 4d }, fixture.Values);
        Advance(fixture.Vm, 10.5);
        Assert.Equal(PlaybackState.Completed, fixture.Vm.State);
        Assert.False(fixture.Bus.IsPlaybackMode);
    });

    [Fact]
    public Task ChangingSpeedPreservesElapsedPositionAndIgnoresSelectedNominalRate() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 2);
        fixture.Vm.SelectedSession = new() { SampleRate = 0.001 }; // Loaded data must keep its own timing.
        fixture.Vm.PlaybackSpeed = 5;
        Assert.InRange(Field<TimeSpan>(fixture.Vm, "_positionAtTimerStart").TotalSeconds, 1.9, 2.1);
        Advance(fixture.Vm, 0.3);
        Assert.Equal(new[] { 0d }, fixture.Values); // 2 + 0.3*5 = 3.5 seconds.
        Advance(fixture.Vm, 0.5);
        Assert.Equal(new[] { 0d, 4d }, fixture.Values);
        Assert.Equal(PlaybackState.Playing, fixture.Vm.State);
        Advance(fixture.Vm, 1.7);
        Assert.Equal(new[] { 0d, 4d, 10d }, fixture.Values);
        Assert.False(fixture.Bus.IsPlaybackMode);
        Assert.Equal(0.001, fixture.Vm.SelectedSession.SampleRate);
    });

    [Fact]
    public Task InvalidSpeedsFallBackToOneWithoutLosingElapsedPosition() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            fixture.Displayed.Clear();
            await fixture.Vm.PlayCommand.ExecuteAsync(null);
            Advance(fixture.Vm, 2);
            fixture.Vm.PlaybackSpeed = invalid;
            Assert.Equal(1, fixture.Vm.PlaybackSpeed);
            Advance(fixture.Vm, 1);
            Assert.Equal(new[] { 0d }, fixture.Values);
            Advance(fixture.Vm, 2.5);
            Assert.Equal(new[] { 0d, 4d }, fixture.Values);
            fixture.Vm.StopCommand.Execute(null);
            Assert.False(fixture.Bus.IsPlaybackMode);
        }
    });

    [Fact]
    public Task SubMillisecondFinalTimestampCompletesWithoutRoundingAwayTheLastReading() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync([TimeSpan.Zero, TimeSpan.FromTicks(3000001)]);
        await fixture.Vm.PlayCommand.ExecuteAsync(null);
        Advance(fixture.Vm, 1);
        Assert.Equal(2, fixture.Displayed.Count);
        Assert.Equal(3000001, (fixture.Displayed[^1].Timestamp - fixture.Displayed[0].Timestamp).Ticks);
        Assert.Equal(PlaybackState.Completed, fixture.Vm.State);
        Assert.Equal(1, fixture.Vm.Progress);
        Assert.False(fixture.Bus.IsPlaybackMode);
    });

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Advance(HistoryPlaybackViewModel vm, double elapsedSeconds)
    {
        // Keep WPF dispatch deterministic while exercising the production tick and speed calculation.
        Field<DispatcherTimer?>(vm, "_playbackTimer")?.Stop();
        var clock = Field<Stopwatch>(vm, "_playbackClock");
        clock.Reset();
        typeof(Stopwatch).GetField("_elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(clock, (long)(elapsedSeconds * Stopwatch.Frequency));
        clock.Start();
        typeof(HistoryPlaybackViewModel).GetMethod("OnPlaybackTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object?[] { null, EventArgs.Empty });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required HistoryPlaybackViewModel Vm { get; init; }
        public required DataBus Bus { get; init; }
        public required SqliteStorageService Storage { get; init; }
        public required string Path { get; init; }
        public List<MagnetometerReading> Displayed { get; } = [];
        public IEnumerable<double> Values => Displayed.Select(reading => reading.ChannelValues[0]);

        public static async Task<Fixture> CreateAsync(TimeSpan[]? offsets = null)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"playback-clock-{Guid.NewGuid():N}.db");
            var database = new DatabaseInitializer(path);
            await database.InitializeAsync();
            var bus = new DataBus();
            var storage = new SqliteStorageService(database, bus);
            string session = await storage.StartSessionAsync("recorded-time", new SensorConfig
            {
                SampleRate = 1000, ChannelCountOverride = 3,
                ChannelNamesOverride = ["X", "Y", "Z"], ChannelUnitsOverride = ["nT", "nT", "nT"],
            }, new ConnectionConfig());
            DateTime start = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            offsets ??= [TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];
            await storage.SaveReadingsAsync(offsets.Select(offset => new MagnetometerReading
            {
                SessionId = session, Timestamp = start + offset, ChannelValues = [offset.TotalSeconds, 0, 0],
            }));
            await storage.EndSessionAsync(session);
            var vm = new HistoryPlaybackViewModel(storage, bus, new OrthogonalityCorrector(), new SqliteCalibrationRepository(database));
            vm.SelectedSession = Assert.Single(await storage.GetSessionsAsync());
            await vm.LoadSessionCommand.ExecuteAsync(null);
            var fixture = new Fixture { Vm = vm, Bus = bus, Storage = storage, Path = path };
            bus.ReadingReceived += fixture.Displayed.Add;
            return fixture;
        }

        public ValueTask DisposeAsync()
        {
            Vm.StopCommand.Execute(null);
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
            return ValueTask.CompletedTask;
        }
    }
}
