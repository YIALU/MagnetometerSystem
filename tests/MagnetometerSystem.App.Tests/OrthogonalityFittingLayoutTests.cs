using System.IO;
using System.Reflection;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class OrthogonalityFittingLayoutTests
{
    [Fact]
    public Task StartCollectingRejectsExtraMissingAndMixedChannelsWithoutTakingAPrefix() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            foreach (var (dual, units) in new[]
            {
                (false, new[] { "nT", "nT", "nT", "nT" }),
                (false, new[] { "°C", "nT", "nT", "nT" }),
                (false, new[] { "nT", "nT" }),
                (false, new[] { "nT", "", "nT" }),
                (false, new[] { "nT", "uT", "nT" }),
                (true, new[] { "nT", "nT", "nT", "nT", "nT", "nT", "°C" }),
            })
            {
                var vm = fixture.CreateVm(dual ? 6 : 3);
                await fixture.PrepareLiveAsync(units);
                vm.StartCollectingCommand.Execute(null);
                Assert.False(vm.IsCollecting);
                Assert.Contains("CSV", vm.CollectionStatus);
                fixture.Bus.PublishReading(new MagnetometerReading
                { Timestamp = DateTime.UtcNow, ChannelValues = Enumerable.Repeat(42d, units.Length).ToArray() });
                Assert.Empty(vm.CollectedData);
                Assert.Equal(0, vm.CollectedSampleCount);
                Assert.Null(vm.CalculationResult);
            }
        });

    [Theory]
    [InlineData(3, false)]
    [InlineData(6, true)]
    public Task ExactStandardLayoutStillCollectsInContinuousAndManualModes(int channels, bool manual) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(channels);
            vm.SelectedMode = manual ? CalibrationCollectionMode.Manual48 : CalibrationCollectionMode.Continuous;
            await fixture.PrepareLiveAsync(Enumerable.Range(0, channels)
                .Select(i => i % 2 == 0 ? "µT" : "uT").ToArray());
            vm.StartCollectingCommand.Execute(null);
            Assert.True(vm.IsCollecting, vm.CollectionStatus);

            int samples = manual ? 10 : 1;
            for (int i = 0; i < samples; i++)
                fixture.Bus.PublishReading(new MagnetometerReading
                {
                    Timestamp = DateTime.UtcNow,
                    ChannelValues = Enumerable.Range(0, channels).Select(ch => 10d * i + ch).ToArray(),
                });
            if (manual) vm.RecordCurrentPointCommand.Execute(null);

            double mean = manual ? 45 : 0;
            Assert.Equal(new[] { mean, mean + 1, mean + 2 }, Assert.Single(vm.CollectedData));
            Assert.Equal(1, vm.CollectedSampleCount);
            Assert.Equal("uT", vm.CollectedUnit);
            if (channels == 6)
                Assert.Equal(new[] { mean + 3, mean + 4, mean + 5 }, Assert.Single(SecondGroup(vm)));
            vm.StopCollectingCommand.Execute(null);
            Assert.False(vm.IsCollecting);
        });

    [Theory]
    [InlineData("extra-reading")]
    [InlineData("short-reading")]
    [InlineData("new-protocol")]
    public Task ChangedLayoutStopsOnlyFittingAndDoesNotAppendPartialAxes(string change) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "nT", "nT"]);
            int rawReadings = 0;
            int acquisitionFaults = 0;
            fixture.Bus.ReadingReceived += _ => rawReadings++;
            fixture.Bus.AcquisitionFaulted += _ => acquisitionFaults++;
            vm.StartCollectingCommand.Execute(null);
            fixture.Bus.PublishReading(new MagnetometerReading { ChannelValues = [1, 2, 3], Timestamp = DateTime.UtcNow });
            Assert.Single(vm.CollectedData);

            if (change == "new-protocol")
                await fixture.PrepareLiveAsync(["nT", "nT", "nT", "nT"]);
            fixture.Bus.PublishReading(new MagnetometerReading
            {
                Timestamp = DateTime.UtcNow,
                ChannelValues = change switch { "extra-reading" => [4, 5, 6, 7], "short-reading" => [4, 5], _ => [4, 5, 6] },
            });

            Assert.False(vm.IsCollecting);
            Assert.Contains("CSV", vm.CollectionStatus);
            Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
            Assert.Equal(1, vm.CollectedSampleCount);
            fixture.Bus.PublishReading(new MagnetometerReading { ChannelValues = [8, 9, 10], Timestamp = DateTime.UtcNow });
            Assert.Equal(3, rawReadings);
            Assert.Equal(0, acquisitionFaults);
            Assert.NotNull(fixture.Bus.CurrentConnection);
            Assert.Single(vm.CollectedData);
        });

    [Fact]
    public Task HistoricalImportRequiresConsistentCompleteLayoutAndPreservesPriorDatasetOnRejection() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            foreach (int channels in new[] { 3, 6 })
            {
                var vm = fixture.CreateVm(channels);
                var session = await fixture.SaveSessionAsync(Enumerable.Repeat("nT", channels).ToArray());
                await ImportSessionAsync(vm, session);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                if (channels == 6)
                    Assert.Equal(new double[] { 4, 5, 6 }, Assert.Single(SecondGroup(vm)));
                Assert.Equal("nT", vm.CollectedUnit);

                session.ChannelCount = channels + 1;
                await ImportSessionAsync(vm, session);
                Assert.Contains("CSV", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                session.ChannelCount = channels;

                var extra = await fixture.SaveSessionAsync(Enumerable.Repeat("nT", channels + 1).ToArray());
                await ImportSessionAsync(vm, extra);
                Assert.Contains("CSV", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));

                // Exercise the same production import with a legacy/inconsistent metadata snapshot.
                // The underlying rows still come from real SQLite and contain the extra channel.
                extra.ChannelCount = channels;
                extra.ChannelUnits = Enumerable.Repeat("nT", channels).ToArray();
                await ImportSessionAsync(vm, extra);
                Assert.Contains("读数与通道元数据不一致", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                Assert.Equal(channels + 1, Assert.Single(await fixture.Storage.GetReadingsAsync(extra.Id)).ChannelValues.Length);

                session.ChannelUnits = [];
                await ImportSessionAsync(vm, session);
                Assert.Contains("CSV", vm.CollectionStatus);
                session.ChannelUnits = Enumerable.Repeat("nT", channels).ToArray();
                session.ChannelUnits[^1] = "°C";
                await ImportSessionAsync(vm, session);
                Assert.Contains("单位", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                Assert.Equal(1, vm.CollectedSampleCount);
                Assert.Null(vm.CalculationResult);
                Assert.Equal(1, Assert.Single((await fixture.Storage.GetSessionsAsync()).Where(s => s.Id == session.Id)).TotalReadings);
            }
        });

    [Fact]
    public Task HistoricalImportFitsOriginalValuesOfCorrectedReadings() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            string id = await fixture.Storage.StartSessionAsync("corrected input", new SensorConfig
            {
                Type = SensorType.Generic, ChannelCountOverride = 3,
                ChannelNamesOverride = ["X", "Y", "Z"], ChannelUnitsOverride = ["nT", "nT", "nT"],
            }, new ConnectionConfig());
            await fixture.Storage.SaveReadingsAsync([new MagnetometerReading
            {
                SessionId = id, Timestamp = DateTime.UtcNow, ChannelValues = [11, 12, 13],
                OriginalChannelValues = [1, 2, 3], IsOrthogonalityCorrected = true,
            }]);
            await fixture.Storage.EndSessionAsync(id);
            var stored = Assert.Single(await fixture.Storage.GetReadingsAsync(id));
            Assert.Equal(new double[] { 1, 2, 3 }, stored.OriginalChannelValues);

            await ImportSessionAsync(vm, Assert.Single((await fixture.Storage.GetSessionsAsync()).Where(s => s.Id == id)));

            // 已校正读数用原始值拟合，不在上一次校正的结果上再拟合。
            Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
        });

    private static List<double[]> SecondGroup(OrthogonalityCalibrationViewModel vm) =>
        (List<double[]>)typeof(OrthogonalityCalibrationViewModel)
            .GetField("_collectedDataSecondGroup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    private static Task ImportSessionAsync(OrthogonalityCalibrationViewModel vm, SessionInfo session) =>
        (Task)typeof(OrthogonalityCalibrationViewModel)
            .GetMethod("LoadSessionDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [session])!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"fitting-layout-{Guid.NewGuid():N}.db");
        private readonly List<OrthogonalityCalibrationViewModel> _vms = [];
        private DatabaseInitializer _database = null!;
        public DataBus Bus { get; } = new();
        public SqliteStorageService Storage { get; private set; } = null!;
        private SqliteCalibrationRepository Profiles { get; set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture._database = new DatabaseInitializer(fixture._path);
            await fixture._database.InitializeAsync();
            fixture.Storage = new SqliteStorageService(fixture._database, fixture.Bus);
            fixture.Profiles = new SqliteCalibrationRepository(fixture._database);
            return fixture;
        }

        public OrthogonalityCalibrationViewModel CreateVm(int channels)
        {
            var vm = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), Profiles, Bus, Storage)
            { SelectedSensorType = channels == 6 ? SensorType.DualTriaxialFluxgate : SensorType.TriaxialFluxgate };
            // Keep the real collection command and raw CSV writer, but never create a file
            // in the current user's calibration_raw directory during this regression.
            typeof(OrthogonalityCalibrationViewModel).GetField("_rawWriter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, new StreamWriter(new MemoryStream()));
            _vms.Add(vm);
            return vm;
        }

        public async Task PrepareLiveAsync(string[] units)
        {
            await Bus.PublishAcquisitionStartingAsync(Config(units));
            Bus.PublishConnectionChanged(new LiveConnection());
        }

        public async Task<SessionInfo> SaveSessionAsync(string[] units)
        {
            string id = await Storage.StartSessionAsync("fitting input", Config(units), new ConnectionConfig());
            await Storage.SaveReadingsAsync([new MagnetometerReading
            { SessionId = id, Timestamp = DateTime.UtcNow, ChannelValues = Enumerable.Range(1, units.Length).Select(i => (double)i).ToArray() }]);
            await Storage.EndSessionAsync(id);
            return Assert.Single((await Storage.GetSessionsAsync()).Where(s => s.Id == id));
        }

        private static SensorConfig Config(string[] units) => new()
        {
            Type = SensorType.Generic, ChannelCountOverride = units.Length,
            ChannelNamesOverride = Enumerable.Range(0, units.Length).Select(i => $"CH{i}").ToArray(),
            ChannelUnitsOverride = units,
        };

        public void Dispose()
        {
            foreach (var vm in _vms) vm.Cleanup();
            Storage.Dispose();
            using (var connection = new SqliteConnection(_database.ConnectionString))
            {
                connection.Open();
                SqliteConnection.ClearPool(connection);
            }
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }

    private sealed class LiveConnection : IDeviceConnection
    {
        public event EventHandler<byte[]>? DataReceived { add { } remove { } }
        public event EventHandler<string>? ErrorOccurred { add { } remove { } }
        public event EventHandler<bool>? ConnectionStateChanged { add { } remove { } }
        public bool IsConnected => true;
        public ConnectionConfig Config { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
