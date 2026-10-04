using System.Diagnostics;
using System.IO;
using System.Windows;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class HistoryPlaybackViewModelTests
{
    [Fact]
    public Task HistoryPage_BindsEmbeddedChart_AndKeepsTemperatureOnItsOwnAxis() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            using var chart = new RealtimeChartViewModel(fixture.Bus);
            var view = new HistoryPlaybackView { DataContext = vm };
            var window = new Window
            {
                Content = view, DataContext = new { RealtimeChartVM = chart }, Width = 1280, Height = 900,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            var errors = new StringWriter();
            using var listener = new TextWriterTraceListener(errors);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            try
            {
                window.Show();
                await WpfTestHost.PumpAsync();
                await vm.LoadSessionByIdAsync(fixture.SessionId);
                vm.SeekTo(0);
                await WpfTestHost.PumpAsync();
                chart.RefreshPlot();

                Assert.NotNull(chart.PlotControl);
                var lines = chart.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().ToArray();
                Assert.Equal(4, lines.Length);
                var temperature = Assert.Single(lines.Where(line => line.LegendText.StartsWith("Temperature")));
                Assert.NotSame(chart.PlotControl.Plot.Axes.Left, temperature.Axes.YAxis);
                Assert.Equal("°C", temperature.Axes.YAxis.Label.Text);
                Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
            }
            finally
            {
                window.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }
        });

    [Fact]
    public Task LoadAndSeek_PreserveProtocolChannelsAndUnits_WithoutPublishingRawOrWritingDatabase() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            SensorConfig? chartConfig = null;
            var displayed = new List<MagnetometerReading>();
            var rawCount = 0;
            fixture.Bus.AcquisitionStarted += config => chartConfig = config;
            fixture.Bus.ProcessedReadingReceived += displayed.Add;
            fixture.Bus.ReadingReceived += _ => rawCount++;
            // Changing the next selection must not reinterpret the already loaded data.
            vm.SelectedSession = new SessionInfo { Name = "not loaded", ChannelCount = 1, SampleRate = 1 };

            vm.SeekTo(1);

            Assert.NotNull(chartConfig);
            Assert.Equal(SensorType.Generic, chartConfig.Type);
            Assert.Equal(new[] { "Temperature", "Bx", "By", "Bz" }, chartConfig.ChannelNames);
            Assert.Equal(new[] { "°C", "nT", "nT", "nT" }, chartConfig.ChannelUnits);
            Assert.Equal(new double[] { 22, 3, 4, 5 }, Assert.Single(displayed).ChannelValues);
            Assert.Equal(0, rawCount);
            Assert.Equal(3, (await fixture.Storage.GetReadingsAsync(fixture.SessionId)).Count);
            Assert.Equal(3, Assert.Single(await fixture.Storage.GetSessionsAsync()).TotalReadings);
            Assert.Equal(PlaybackState.Paused, vm.State);
        });

    [Fact]
    public Task Correction_UsesExplicitIndices_LeavesTemperatureAndStoredOriginalsUnchanged() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            vm.IsOrthogonalityCorrectionEnabled = true;
            vm.FirstChannelIndices = "1,2,3";
            vm.SelectedOrthogonalityProfile = new OrthogonalityParams
            {
                CompensationMatrix = [2, 0, 0, 0, 2, 0, 0, 0, 2],
            };
            var displayed = new List<MagnetometerReading>();
            fixture.Bus.ProcessedReadingReceived += displayed.Add;

            vm.SeekTo(0);

            var reading = Assert.Single(displayed);
            Assert.Equal(new double[] { 20, 2, 4, 6 }, reading.ChannelValues);
            Assert.Equal(new double[] { 20, 1, 2, 3 }, reading.OriginalChannelValues);
            Assert.True(reading.IsOrthogonalityCorrected);
            var stored = await fixture.Storage.GetReadingsAsync(fixture.SessionId);
            Assert.Equal(new double[] { 20, 1, 2, 3 }, stored[0].ChannelValues);
            Assert.False(stored[0].IsOrthogonalityCorrected);
            Assert.False(await fixture.Storage.HasCorrectedReadingsAsync(fixture.SessionId));

            vm.FirstChannelIndices = "1,1,3";
            vm.SeekTo(0.5);
            Assert.Single(displayed);
            Assert.Contains("校正配置无效", vm.StatusMessage);

            vm.FirstChannelIndices = "0,1,2";
            vm.SeekTo(0.5);
            Assert.Single(displayed);
            Assert.Contains("相同的磁场单位", vm.StatusMessage);
        });

    [Fact]
    public Task CurrentConnection_BlocksPlaybackAndSeek_EvenWhileDisconnectedForReconnect() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            await using var connection = new TcpDeviceConnection(new ConnectionConfig
            {
                Type = ConnectionType.Tcp, IpAddress = "127.0.0.1", Port = 12345,
            });
            fixture.Bus.PublishConnectionChanged(connection);
            var displayed = new List<MagnetometerReading>();
            var stopped = 0;
            fixture.Bus.ProcessedReadingReceived += displayed.Add;
            fixture.Bus.AcquisitionStopped += () => stopped++;

            Assert.False(connection.IsConnected);
            Assert.False(vm.PlayCommand.CanExecute(null));
            await vm.PlayCommand.ExecuteAsync(null);
            vm.SeekTo(0.5);
            vm.StopCommand.Execute(null);

            Assert.Empty(displayed);
            Assert.Equal(0, stopped);
            Assert.False(fixture.Bus.IsPlaybackMode);
            fixture.Bus.PublishConnectionChanged(null);
            Assert.True(vm.PlayCommand.CanExecute(null));
        });

    [Fact]
    public Task Playback_UsesRecordedTimeAtChosenSpeed_CompletesAndCanRestart() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            vm.PlaybackSpeed = 5;
            var displayed = new List<MagnetometerReading>();
            var rawCount = 0;
            var starts = 0;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Bus.ProcessedReadingReceived += displayed.Add;
            fixture.Bus.ReadingReceived += _ => rawCount++;
            fixture.Bus.AcquisitionStarted += _ => starts++;
            fixture.Bus.AcquisitionStopped += () => completion.TrySetResult();
            var clock = Stopwatch.StartNew();

            await vm.PlayCommand.ExecuteAsync(null);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The stored span is 1 second, while nominal SampleRate is deliberately 1000 Hz.
            // At 5x the last sample cannot be emitted after only a few nominal sample ticks.
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(180), clock.Elapsed.ToString());
            Assert.Equal(3, displayed.Count);
            Assert.Equal(PlaybackState.Completed, vm.State);
            Assert.Equal(1, vm.Progress);
            Assert.False(fixture.Bus.IsPlaybackMode);
            Assert.Equal(0, rawCount);

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await vm.PlayCommand.ExecuteAsync(null);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, starts);
            Assert.Equal(6, displayed.Count);
            Assert.False(fixture.Bus.IsPlaybackMode);
            Assert.Equal(3, (await fixture.Storage.GetReadingsAsync(fixture.SessionId)).Count);
        });

    [Fact]
    public Task StartingLiveAcquisition_ReleasesPlaybackWithoutStoppingTheNewAcquisition() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            vm.SeekTo(0.5);
            Assert.True(fixture.Bus.IsPlaybackMode);
            var stopped = 0;
            fixture.Bus.AcquisitionStopped += () => stopped++;

            await fixture.Bus.PublishAcquisitionStartingAsync(new SensorConfig());

            Assert.False(fixture.Bus.IsPlaybackMode);
            Assert.Equal(PlaybackState.Ready, vm.State);
            Assert.Equal(0, vm.CurrentIndex);
            Assert.Equal(0, stopped);
        });

    [Fact]
    public Task LegacyMissingUnits_AreNotInventedAsMagneticUnits() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await PlaybackFixture.CreateAsync();
            using (var connection = new SqliteConnection(fixture.Database.ConnectionString))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE sessions SET channel_units = '[]' WHERE id = $id";
                command.Parameters.AddWithValue("$id", fixture.SessionId);
                await command.ExecuteNonQueryAsync();
            }
            using var vm = fixture.CreateViewModel();
            await vm.LoadSessionByIdAsync(fixture.SessionId);
            SensorConfig? chartConfig = null;
            fixture.Bus.AcquisitionStarted += config => chartConfig = config;

            vm.SeekTo(0);

            Assert.NotNull(chartConfig);
            Assert.Equal(new[] { "", "", "", "" }, chartConfig.ChannelUnits);
            var displayed = 0;
            fixture.Bus.ProcessedReadingReceived += _ => displayed++;
            vm.IsOrthogonalityCorrectionEnabled = true;
            vm.SelectedOrthogonalityProfile = new OrthogonalityParams();
            vm.FirstChannelIndices = "1,2,3";
            vm.SeekTo(0.5);
            Assert.Equal(0, displayed);
            Assert.Contains("缺少有效单位", vm.StatusMessage);
        });

    private sealed class PlaybackFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"history_{Guid.NewGuid():N}.db");
        public DataBus Bus { get; } = new();
        public DatabaseInitializer Database { get; private set; } = null!;
        public SqliteStorageService Storage { get; private set; } = null!;
        public string SessionId { get; private set; } = "";

        public static async Task<PlaybackFixture> CreateAsync()
        {
            var fixture = new PlaybackFixture();
            fixture.Database = new DatabaseInitializer(fixture._path);
            await fixture.Database.InitializeAsync();
            fixture.Storage = new SqliteStorageService(fixture.Database, fixture.Bus);
            fixture.SessionId = await fixture.Storage.StartSessionAsync("four protocol channels", new SensorConfig
            {
                Type = SensorType.Generic,
                SampleRate = 1000,
                ChannelCountOverride = 4,
                ChannelNamesOverride = ["Temperature", "Bx", "By", "Bz"],
                ChannelUnitsOverride = ["°C", "nT", "nT", "nT"],
            }, new ConnectionConfig());
            var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
            await fixture.Storage.SaveReadingsAsync(Enumerable.Range(0, 3).Select(i => new MagnetometerReading
            {
                SessionId = fixture.SessionId,
                Timestamp = start.AddMilliseconds(i * 500),
                SensorType = SensorType.Generic,
                ChannelValues = [20 + i, 1 + i, 2 + i, 3 + i],
            }));
            await fixture.Storage.EndSessionAsync(fixture.SessionId);
            return fixture;
        }

        public HistoryPlaybackViewModel CreateViewModel() => new(Storage, Bus,
            new OrthogonalityCorrector(), new SqliteCalibrationRepository(Database));

        public void Dispose()
        {
            Storage.Dispose();
            using (var connection = new SqliteConnection(Database.ConnectionString))
            {
                connection.Open();
                SqliteConnection.ClearPool(connection);
            }
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        }
    }
}
