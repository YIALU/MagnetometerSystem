using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class OrthogonalityUnitWorkflowTests
{
    [Theory]
    [InlineData("nT", "uT", false)]
    [InlineData("nT", "mT", false)]
    [InlineData("nT", "T", false)]
    [InlineData("", "nT", false)]
    [InlineData("nT", "nT", true)]
    [InlineData("uT", "µT", true)]
    public Task LiveCorrection_ValidatesProfileUnitWhileRawReadingsAlwaysReachSqlite(string profileUnit, string inputUnit, bool applied) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var sessions = new SessionListViewModel(fixture.Storage, new CsvExporter(fixture.Storage), fixture.Bus,
                new OrthogonalityCorrector(), fixture.Profiles);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var vm = new ConnectionViewModel(new ConnectionFactory(), fixture.Bus, new OrthogonalityCorrector(), fixture.Profiles)
            {
                SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                IsOrthogonalityCorrectionEnabled = true,
                ActiveOrthogonalityProfile = new OrthogonalityParams { Unit = profileUnit, Offset = [100, 200, 300] },
                FirstOrthogonalityChannelsText = "0,1,2",
                ProtocolConfig = new ProtocolConfig
                {
                    FieldMappings = Enumerable.Range(0, 4).Select(i => new FieldMapping
                    { Name = $"CH{i}", ChannelIndex = i, ByteOffset = i, Unit = i == 3 ? "°C" : inputUnit }).ToList(),
                },
            };
            var displayed = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Bus.ProcessedReadingReceived += reading => displayed.TrySetResult(reading);
            try
            {
                var accept = listener.AcceptTcpClientAsync();
                await vm.ConnectCommand.ExecuteAsync(null);
                Assert.True(vm.IsConnected, vm.LastError);
                using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
                await peer.GetStream().WriteAsync("1,2,3,25\n"u8.ToArray());
                var display = await displayed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await vm.StopAcquisitionAsync();
                await WpfTestHost.PumpAsync();

                var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
                var raw = Assert.Single(await fixture.Storage.GetReadingsAsync(session.Id));
                Assert.Equal(new double[] { 1, 2, 3, 25 }, raw.ChannelValues);
                Assert.False(raw.IsOrthogonalityCorrected);
                Assert.Equal(inputUnit, session.ChannelUnits[0]);
                Assert.Equal(applied, display.IsOrthogonalityCorrected);
                Assert.Equal(applied ? new double[] { -99, -198, -297, 25 } : raw.ChannelValues, display.ChannelValues);
                if (applied) Assert.Equal(raw.ChannelValues, display.OriginalChannelValues);
                else
                {
                    Assert.Contains("改正未应用", vm.LastError);
                    Assert.Contains("单位", vm.LastError);
                }
                Assert.Null(sessions.ActiveSessionId);
            }
            finally { await vm.StopAcquisitionAsync(); listener.Stop(); }
        });

    [Theory]
    [InlineData("uT")]
    [InlineData("mT")]
    [InlineData("T")]
    public Task PlaybackCorrection_RejectsNanoteslaProfileForOtherStoredUnits(string inputUnit) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        string id = await fixture.Storage.StartSessionAsync("history", new SensorConfig
        {
            ChannelCountOverride = 4, ChannelNamesOverride = ["X", "Y", "Z", "T"],
            ChannelUnitsOverride = [inputUnit, inputUnit, inputUnit, "°C"],
        }, new ConnectionConfig());
        await fixture.Storage.SaveReadingsAsync([new MagnetometerReading
        { SessionId = id, Timestamp = DateTime.UtcNow, ChannelValues = [1, 2, 3, 25] }]);
        await fixture.Storage.EndSessionAsync(id);
        using var vm = new HistoryPlaybackViewModel(fixture.Storage, fixture.Bus, new OrthogonalityCorrector(), fixture.Profiles);
        await vm.LoadSessionByIdAsync(id);
        vm.IsOrthogonalityCorrectionEnabled = true;
        vm.SelectedOrthogonalityProfile = new OrthogonalityParams { Unit = "nT", Offset = [100, 200, 300] };
        vm.FirstChannelIndices = "0,1,2";
        var display = new List<MagnetometerReading>();
        fixture.Bus.ProcessedReadingReceived += display.Add;

        vm.SeekTo(0);

        Assert.Empty(display);
        Assert.Contains("单位", vm.StatusMessage);
        Assert.False(fixture.Bus.IsPlaybackMode);
        Assert.Equal(new double[] { 1, 2, 3, 25 }, Assert.Single(await fixture.Storage.GetReadingsAsync(id)).ChannelValues);
        Assert.False(await fixture.Storage.HasCorrectedReadingsAsync(id));
    });

    [Fact]
    public void BatchCorrectionMapping_ValidatesEachProfileAgainstItsOwnGroup()
    {
        var session = new SessionInfo
        { ChannelCount = 6, ChannelUnits = ["nT", "nT", "nT", "µT", "μT", "uT"] };
        var map = typeof(SessionListViewModel).GetMethod("CorrectionMapping", BindingFlags.NonPublic | BindingFlags.Static)!;
        int[] Invoke(string channels, bool second, string unit) =>
            (int[])map.Invoke(null, [channels, session, second, new OrthogonalityParams { Unit = unit, Offset = [100, 200, 300] }])!;

        Assert.Equal(new[] { 0, 1, 2 }, Invoke("0,1,2", false, "nT"));
        Assert.Equal(new[] { 3, 4, 5 }, Invoke("3,4,5", true, "uT"));
        Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => Invoke("3,4,5", true, "nT")).InnerException);
        Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => Invoke("0,1,2", false, "")).InnerException);
    }

    [Fact]
    public Task FitQualityRating_UsesTheSamePhysicalResidualForBothGroupsInEveryUnit() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var vm = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), fixture.Profiles, fixture.Bus, fixture.Storage);
        foreach (var (unit, scale) in new[] { ("nT", 1d), ("uT", 1e3), ("mT", 1e6), ("T", 1e9) })
        {
            vm.CalculationResult = new OrthogonalityResult
            {
                Parameters = new OrthogonalityParams { Unit = unit },
                Quality = new FitQuality { ResidualStd = 20 / scale }
            };
            vm.SecondCalculationResult = new OrthogonalityResult
            {
                Parameters = new OrthogonalityParams { Unit = unit },
                Quality = new FitQuality { ResidualStd = 1000 / scale }
            };
            Assert.Equal("良好", vm.QualityRating);
            Assert.Equal("较差", vm.SecondQualityRating);
        }
    });

    [Fact]
    public Task FittingViewModel_KeepsCollectedUnitWhenSelectionChangesAndSavesThatUnit() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var vm = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), fixture.Profiles, fixture.Bus, fixture.Storage);
        SetCollectedData(vm, "uT");
        vm.FittingUnit = "mT";

        await vm.RunCalculationCommand.ExecuteAsync(null);

        Assert.NotNull(vm.CalculationResult);
        Assert.True(vm.CalculationResult.Success, vm.CalculationStatus);
        Assert.Equal("uT", vm.CalculationResult.Parameters.Unit);
        Assert.Equal("uT", vm.CollectedUnit);
        vm.FittingUnit = "T";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        var saved = Assert.Single(await fixture.Profiles.GetOrthogonalityProfilesAsync());
        Assert.Equal("uT", saved.Unit);
        Assert.Equal(vm.CalculationResult.Parameters.Offset, saved.Offset);
    });

    [Fact]
    public Task FittingViewModel_DiscardsInFlightResultAfterCollectedDataUnitChanges() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var service = new BlockingFitService();
        var vm = new OrthogonalityCalibrationViewModel(service, fixture.Profiles, fixture.Bus, fixture.Storage);
        SetCollectedData(vm, "uT");
        vm.ReferenceFieldStrength = 50;
        var calculation = vm.RunCalculationCommand.ExecuteAsync(null);
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            SetCollectedData(vm, "nT");
            Assert.Equal("nT", vm.CollectedUnit);
            Assert.True(vm.ReferenceFieldStrength is null or 50000d,
                "Changing uT to nT must clear or convert the old 50 uT reference field.");
            service.Release.Set();
            await calculation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("uT", service.InputUnit);
            Assert.Null(vm.CalculationResult);
            Assert.Null(vm.SecondCalculationResult);
            Assert.Null(vm.VisualizationCorrectedData);
            Assert.Equal("nT", vm.CollectedUnit);
        }
        finally { service.Release.Set(); await calculation; }
    });

    // The same replacement session/CSV import performs; avoiding native file pickers
    // lets the test control a dataset replacement while the actual VM calculation is awaiting Task.Run.
    private static void SetCollectedData(OrthogonalityCalibrationViewModel vm, string unit)
    {
        var data = new List<double[]>();
        var random = new Random(42);
        var strength = unit == "uT" ? 50d : 50000d;
        for (var i = 0; i < 600; i++)
        {
            var z = 2 * random.NextDouble() - 1;
            var angle = 2 * Math.PI * random.NextDouble();
            var radius = Math.Sqrt(1 - z * z);
            data.Add([strength * radius * Math.Cos(angle) + strength * 0.002,
                strength * radius * Math.Sin(angle) - strength * 0.001, strength * z + strength * 0.004]);
        }
        vm.ReplaceSamples(data, [], unit, 3);
    }

    private sealed class BlockingFitService : IOrthogonalityService, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public string? InputUnit { get; private set; }
        public OrthogonalityResult Calculate(double[,] rawData, double? referenceFieldStrength = null, string? unit = null)
        {
            InputUnit = unit;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fit test barrier");
            return new OrthogonalityResult { Success = true, Parameters = new OrthogonalityParams { Unit = unit ?? "" } };
        }
        public double[] Apply(OrthogonalityParams parameters, double x, double y, double z) => throw new NotSupportedException();
        public FitQuality EvaluateFit(OrthogonalityParams parameters, double[,] rawData) => throw new NotSupportedException();
        public void Dispose() => Release.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"unit-workflow-{Guid.NewGuid():N}.db");
        private DatabaseInitializer _database = null!;
        public DataBus Bus { get; } = new();
        public SqliteStorageService Storage { get; private set; } = null!;
        public SqliteCalibrationRepository Profiles { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture._database = new DatabaseInitializer(fixture._path);
            await fixture._database.InitializeAsync();
            fixture.Storage = new SqliteStorageService(fixture._database, fixture.Bus);
            fixture.Profiles = new SqliteCalibrationRepository(fixture._database);
            return fixture;
        }
        public void Dispose()
        {
            Storage.Dispose();
            using (var connection = new SqliteConnection(_database.ConnectionString))
            {
                connection.Open();
                SqliteConnection.ClearPool(connection);
            }
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
