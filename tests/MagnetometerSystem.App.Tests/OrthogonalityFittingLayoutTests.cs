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
    public Task AmbiguousLayoutsWaitForTheUserInsteadOfTakingAPrefix() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            foreach (var units in new[]
            {
                new[] { "nT", "nT", "nT", "nT" },   // 多于所需，名称看不出轴
                new[] { "nT", "nT" },
                new[] { "nT", "", "nT" },
                new[] { "nT", "uT", "nT" },
            })
            {
                var vm = fixture.CreateVm(3);
                await fixture.PrepareLiveAsync(units);
                Assert.Equal(units.Length, vm.FittingChannelOptions.Count);
                Assert.Equal(-1, vm.FitX1);
                Assert.False(vm.IsFittingChannelValid);
                vm.StartCollectingCommand.Execute(null);
                Assert.False(vm.IsCollecting);
                Assert.Contains("拟合通道", vm.CollectionStatus);
                fixture.Bus.PublishReading(new MagnetometerReading
                { Timestamp = DateTime.UtcNow, ChannelValues = Enumerable.Repeat(42d, units.Length).ToArray() });
                Assert.Empty(vm.CollectedData);
                Assert.Null(vm.CalculationResult);
            }

            // 用户明确选择后按所选通道采集。
            var chosen = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "nT", "nT", "nT"]);
            (chosen.FitX1, chosen.FitY1, chosen.FitZ1) = (3, 1, 2);
            Assert.True(chosen.IsFittingChannelValid, chosen.FittingChannelHint);
            chosen.StartCollectingCommand.Execute(null);
            Assert.True(chosen.IsCollecting, chosen.CollectionStatus);
            fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [10, 11, 12, 13] });
            Assert.Equal(new double[] { 13, 11, 12 }, Assert.Single(chosen.CollectedData));
            chosen.StopCollectingCommand.Execute(null);

            // 混合单位即使手动选择也拒绝。
            var mixed = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "uT", "nT"]);
            (mixed.FitX1, mixed.FitY1, mixed.FitZ1) = (0, 1, 2);
            mixed.StartCollectingCommand.Execute(null);
            Assert.False(mixed.IsCollecting);
            Assert.Contains("单位", mixed.CollectionStatus);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NonMagneticChannelsAreSkippedWhenTheMagneticLayoutIsExact(bool dual) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(dual ? 6 : 3);
            string[] units = dual ? ["nT", "nT", "nT", "nT", "nT", "nT", "°C"] : ["°C", "nT", "nT", "nT"];
            await fixture.PrepareLiveAsync(units);
            vm.StartCollectingCommand.Execute(null);
            Assert.True(vm.IsCollecting, vm.CollectionStatus);
            fixture.Bus.PublishReading(new MagnetometerReading
            {
                Timestamp = DateTime.UtcNow,
                ChannelValues = dual ? [1, 2, 3, 4, 5, 6, 25] : [25, 1, 2, 3],
            });
            Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
            if (dual) Assert.Equal(new double[] { 4, 5, 6 }, Assert.Single(SecondGroup(vm)));
            vm.StopCollectingCommand.Execute(null);
        });

    [Theory]
    [InlineData("cct5", true)]
    [InlineData("cct5", false)]
    [InlineData("zdz", true)]
    [InlineData("zdz", false)]
    public Task BuiltInProtocolsMapProbesByChannelName(string protocolKind, bool dual) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var protocol = protocolKind == "cct5" ? ProtocolConfig.CreateCct5Gradiometer() : ProtocolConfig.CreateZdzC08();
            var names = protocol.DerivedChannelNames.ToArray();
            var vm = fixture.CreateVm(dual ? 6 : 3);
            vm.SelectedMode = CalibrationCollectionMode.Manual48;
            await fixture.PrepareLiveAsync(protocol.DerivedChannelUnits.ToArray(), names);
            vm.StartCollectingCommand.Execute(null);
            Assert.True(vm.IsCollecting, vm.CollectionStatus);
            for (int i = 0; i < 10; i++)
                fixture.Bus.PublishReading(new MagnetometerReading
                { Timestamp = DateTime.UtcNow, ChannelValues = Enumerable.Range(0, names.Length).Select(ch => 10d * ch).ToArray() });
            vm.RecordCurrentPointCommand.Execute(null);

            double Value(string name) => 10d * Array.IndexOf(names, name);
            Assert.Equal(new[] { Value("X1"), Value("Y1"), Value("Z1") }, Assert.Single(vm.CollectedData));
            if (dual) Assert.Equal(new[] { Value("X2"), Value("Y2"), Value("Z2") }, Assert.Single(SecondGroup(vm)));
            Assert.Contains("X1", vm.LiveValuesText);
            vm.StopCollectingCommand.Execute(null);
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
            Assert.Contains("已停止拟合数据采集", vm.CollectionStatus);
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

                // 会话元数据自相矛盾：拒绝，保留已有样本。
                session.ChannelCount = channels + 1;
                await ImportSessionAsync(vm, session);
                Assert.Contains("不一致", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                session.ChannelCount = channels;

                // 多一个看不出轴的通道：不按前缀截取，等用户在“拟合通道”中选择。
                var extra = await fixture.SaveSessionAsync(Enumerable.Repeat("nT", channels + 1).ToArray());
                await ImportSessionAsync(vm, extra);
                Assert.Contains("拟合通道", vm.CollectionStatus);
                Assert.Equal(channels + 1, vm.FittingChannelOptions.Count);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));

                // 旧库的元数据快照少记了一个通道，但行里仍有：即使选了通道也拒绝。
                extra.ChannelCount = channels;
                extra.ChannelNames = extra.ChannelNames[..channels];
                extra.ChannelUnits = Enumerable.Repeat("nT", channels).ToArray();
                SelectInOrder(vm, channels);
                await ImportSessionAsync(vm, extra);
                Assert.Contains("读数与通道元数据不一致", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                Assert.Equal(channels + 1, Assert.Single(await fixture.Storage.GetReadingsAsync(extra.Id)).ChannelValues.Length);

                session.ChannelUnits = [];
                await ImportSessionAsync(vm, session);
                Assert.Contains("不一致", vm.CollectionStatus);
                session.ChannelUnits = Enumerable.Repeat("nT", channels).ToArray();
                session.ChannelUnits[^1] = "°C";
                SelectInOrder(vm, channels);
                await ImportSessionAsync(vm, session);
                Assert.Contains("单位", vm.CollectionStatus);
                Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
                Assert.Equal(1, vm.CollectedSampleCount);
                Assert.Null(vm.CalculationResult);
                Assert.Equal(1, Assert.Single((await fixture.Storage.GetSessionsAsync()).Where(s => s.Id == session.Id)).TotalReadings);
            }
        });

    [Fact]
    public void ProfileCsvQuotesNamesAndSerialsPerRfc4180()
    {
        var profile = new OrthogonalityParams
        {
            Name = "探头 \"A\", 第 2 组", SensorSerial = "SN-1\n备用", Unit = "nT", SampleCount = 48,
            Offset = [1.5, -2, 3], CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
        };
        var csv = OrthogonalityCalibrationViewModel.BuildProfileCsv(profile);
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(19, rows[0].Count);
        Assert.Equal(19, rows[1].Count); // 引号、逗号与换行都留在字段内，后续数值不错位
        Assert.Equal(profile.Name, rows[1][0]);
        Assert.Equal(profile.SensorSerial, rows[1][1]);
        Assert.Equal("1.5", rows[1][7]);
        Assert.Equal("1", rows[1][18]);
    }

    /// <summary>按 RFC 4180 读取 CSV（引号内的逗号、换行与加倍引号）。</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    [Fact]
    public Task ReferenceUnitTextFollowsTheFittingUnit() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            Assert.Equal("单位待定", vm.ReferenceUnitText);
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            // 参考场强标题与输入框后缀都显示拟合数据单位，避免按 nT 输入 uT 数据。
            vm.FittingUnit = "uT";
            Assert.Equal("uT", vm.ReferenceUnitText);
            Assert.Contains(nameof(vm.ReferenceUnitText), changed);
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

    private static void SelectInOrder(OrthogonalityCalibrationViewModel vm, int channels)
    {
        (vm.FitX1, vm.FitY1, vm.FitZ1) = (0, 1, 2);
        if (channels == 6) (vm.FitX2, vm.FitY2, vm.FitZ2) = (3, 4, 5);
    }

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

        public async Task PrepareLiveAsync(string[] units, string[]? names = null)
        {
            await Bus.PublishAcquisitionStartingAsync(Config(units, names));
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

        private static SensorConfig Config(string[] units, string[]? names = null) => new()
        {
            Type = SensorType.Generic, ChannelCountOverride = units.Length,
            ChannelNamesOverride = names ?? Enumerable.Range(0, units.Length).Select(i => $"CH{i}").ToArray(),
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
