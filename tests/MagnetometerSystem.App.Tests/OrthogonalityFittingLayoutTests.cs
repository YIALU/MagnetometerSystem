using System.IO;
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
                Assert.Equal(units.Length, vm.Fitting.Options.Count);
                Assert.Equal(-1, vm.Fitting.FitX1);
                Assert.False(vm.Fitting.IsValid);
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
            (chosen.Fitting.FitX1, chosen.Fitting.FitY1, chosen.Fitting.FitZ1) = (3, 1, 2);
            Assert.True(chosen.Fitting.IsValid, chosen.Fitting.Hint);
            chosen.StartCollectingCommand.Execute(null);
            Assert.True(chosen.IsCollecting, chosen.CollectionStatus);
            fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [10, 11, 12, 13] });
            Assert.Equal(new double[] { 13, 11, 12 }, Assert.Single(chosen.CollectedData));
            chosen.StopCollectingCommand.Execute(null);

            // 混合单位即使手动选择也拒绝。
            var mixed = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "uT", "nT"]);
            (mixed.Fitting.FitX1, mixed.Fitting.FitY1, mixed.Fitting.FitZ1) = (0, 1, 2);
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
                Assert.Equal(channels + 1, vm.Fitting.Options.Count);
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
    public Task ChangingTheSessionFittingMapBlocksNextUntilSamplesMatchIt() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            string id = await fixture.Storage.StartSessionAsync("四通道", new SensorConfig
            {
                Type = SensorType.Generic, ChannelCountOverride = 4,
                ChannelNamesOverride = ["CH0", "CH1", "CH2", "CH3"], ChannelUnitsOverride = ["nT", "nT", "nT", "nT"],
            }, new ConnectionConfig());
            await fixture.Storage.SaveReadingsAsync(Enumerable.Range(0, 3).Select(i => new MagnetometerReading
            { SessionId = id, Timestamp = DateTime.UtcNow.AddSeconds(i), ChannelValues = [4 * i + 1, 4 * i + 2, 4 * i + 3, 4 * i + 4] }).ToList());
            await fixture.Storage.EndSessionAsync(id);
            await ImportSessionAsync(vm, Assert.Single((await fixture.Storage.GetSessionsAsync()).Where(s => s.Id == id)));
            vm.CurrentStep = 2;

            SelectInOrder(vm, 3);   // 用户选定通道后按所选通道读取
            await WaitUntil(() => vm.CollectedSampleCount == 3);
            Assert.Equal(new double[] { 1, 2, 3 }, vm.CollectedData[0]);
            Assert.True(vm.CanGoNext);

            vm.Fitting.FitX1 = 1;   // 无效（与 Y 重复）：不能重新读取，旧样本也不能继续使用
            Assert.True(vm.FittingMapMismatch);
            Assert.False(vm.CanGoNext);
            Assert.Contains("拟合通道已更改", vm.StepGateText);
            vm.Fitting.FitX1 = 0;   // 改回取样时的通道
            Assert.False(vm.FittingMapMismatch);
            Assert.True(vm.CanGoNext);

            vm.Fitting.FitZ1 = 3;   // 有效的新选择：按新通道重新读取，成功后可继续
            await WaitUntil(() => vm.CollectedData[0][2] == 4);
            Assert.Equal(new double[] { 1, 2, 4 }, vm.CollectedData[0]);
            Assert.True(vm.CanGoNext);
        });

    [Fact]
    public Task ChangingTheLiveFittingMapAfterCollectingBlocksNextWhileTheSameLayoutIsShown() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "nT", "nT", "nT"]);
            SelectInOrder(vm, 3);
            vm.StartCollectingCommand.Execute(null);
            for (int i = 0; i < 3; i++)
                fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [i, i + 10, i + 20, i + 30] });
            vm.StopCollectingCommand.Execute(null);
            vm.CurrentStep = 2;
            Assert.True(vm.CanGoNext);

            vm.Fitting.FitX1 = 3;
            Assert.False(vm.CanGoNext);
            vm.Fitting.FitX1 = 0;
            Assert.True(vm.CanGoNext);

            // 断开后下拉框不再描述这批样本的列：已采集的样本仍可继续使用。
            vm.Fitting.FitX1 = 3;
            Assert.False(vm.CanGoNext);
            fixture.Bus.PublishAcquisitionStopped();
            fixture.Bus.PublishConnectionChanged(null);
            Assert.Empty(vm.Fitting.Options);
            Assert.True(vm.CanGoNext);
            Assert.Equal(new double[] { 0, 10, 20 }, vm.CollectedData[0]);
        });

    [Theory]
    [InlineData("live")]
    [InlineData("switch-source")]
    public Task SlowSessionLoadDoesNotOverwriteNewerSamplesOrSource(string later) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var session = await fixture.SaveSessionAsync(["nT", "nT", "nT"]);
            var gated = new GatedReadings(fixture.Storage);
            var vm = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), fixture.CreateRepository(), fixture.Bus, gated)
            { SelectedSensorType = SensorType.TriaxialFluxgate, RawDataDirectory = fixture.RawDir };
            try
            {
                var pending = ImportSessionAsync(vm, session);
                await gated.Requested.Task;   // 会话读取进行中

                if (later == "live")
                {
                    // 读取期间开始实时采集：新样本不能被迟到的会话结果覆盖。
                    await fixture.PrepareLiveAsync(["nT", "nT", "nT"]);
                    vm.DataSource = CalibrationDataSource.Live;
                    vm.StartCollectingCommand.Execute(null);
                    Assert.True(vm.IsCollecting, vm.CollectionStatus);
                    fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [7, 8, 9] });
                }
                else
                {
                    vm.DataSource = CalibrationDataSource.File;   // 用户已离开会话来源
                }

                gated.Release.SetResult();
                await pending;
                if (later == "live")
                {
                    Assert.Equal(new double[] { 7, 8, 9 }, Assert.Single(vm.CollectedData));
                    Assert.True(vm.IsCollecting);
                    vm.StopCollectingCommand.Execute(null);
                }
                else
                {
                    Assert.Empty(vm.CollectedData);
                    Assert.Equal(CalibrationDataSource.File, vm.DataSource);
                }
            }
            finally { vm.Cleanup(); }
        });

    /// <summary>真实存储外包一层：读取会话读数时暂停，直到测试放行。</summary>
    private sealed class GatedReadings(IDataStorageService inner) : IDataStorageService
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(string sessionId, DateTime? startTime = null, DateTime? endTime = null)
        {
            Requested.TrySetResult();
            await Release.Task;
            return await inner.GetReadingsAsync(sessionId, startTime, endTime);
        }

        public Task<ReadingPage> GetReadingsPageAsync(string sessionId, DateTime startTime, DateTime endTime, ReadingPageCursor? after, int limit) =>
            inner.GetReadingsPageAsync(sessionId, startTime, endTime, after, limit);
        public StorageWriteStatus WriteStatus => inner.WriteStatus;
        public event Action<StorageWriteStatus>? WriteStatusChanged { add => inner.WriteStatusChanged += value; remove => inner.WriteStatusChanged -= value; }
        public Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig) => inner.StartSessionAsync(name, sensorConfig, connectionConfig);
        public Task EndSessionAsync(string sessionId) => inner.EndSessionAsync(sessionId);
        public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings) => inner.SaveReadingsAsync(readings);
        public Task WaitForPendingWritesAsync(int timeoutMs = 5000) => inner.WaitForPendingWritesAsync(timeoutMs);
        public Task RetryPendingWritesAsync() => inner.RetryPendingWritesAsync();
        public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync() => inner.GetSessionsAsync();
        public Task DeleteSessionAsync(string sessionId) => inner.DeleteSessionAsync(sessionId);
        public Task UpdateSessionAsync(string sessionId, string name, string? notes) => inner.UpdateSessionAsync(sessionId, name, notes);
        public Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings) => inner.SaveCorrectedReadingsAsync(readings);
        public Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null) => inner.GetCorrectedReadingsAsync(sessionId, correctionProfileId);
        public Task DeleteCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null) => inner.DeleteCorrectedReadingsAsync(sessionId, correctionProfileId);
        public Task<bool> HasCorrectedReadingsAsync(string sessionId) => inner.HasCorrectedReadingsAsync(sessionId);
        public Task<IReadOnlyList<string>> GetCorrectionVersionIdsAsync(string sessionId) => inner.GetCorrectionVersionIdsAsync(sessionId);
    }

    [Fact]
    public Task SelectingAnotherSessionBlocksNextUntilItLoads() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            var a = await SaveThreeReadingSessionAsync(fixture, "A");
            var b = await SaveThreeReadingSessionAsync(fixture, "B");
            await ImportSessionAsync(vm, a);
            vm.CurrentStep = 2;
            Assert.Equal(3, vm.CollectedSampleCount);
            Assert.True(vm.CanGoNext);

            // 选了会话 B 但读取失败（元数据不一致）：A 的样本保留，但不能当作 B 的数据进入下一步。
            b.ChannelCount = 4;
            await ImportSessionAsync(vm, b);
            Assert.Contains("不一致", vm.CollectionStatus);
            Assert.Equal(3, vm.CollectedSampleCount);
            Assert.False(vm.CanGoNext);
            Assert.Contains("另一个数据来源", vm.StepGateText);

            // B 修正后读取成功：样本换成 B 的，可以继续。
            b.ChannelCount = 3;
            await ImportSessionAsync(vm, b);
            Assert.Contains("B", vm.CollectionStatus);
            Assert.True(vm.CanGoNext);

            // 切到有连接的实时来源：显示的是另一组通道，切回会话来源后恢复。
            await fixture.PrepareLiveAsync(["nT", "nT", "nT"]);
            vm.DataSource = CalibrationDataSource.Live;
            Assert.False(vm.CanGoNext);
            vm.DataSource = CalibrationDataSource.Session;
            Assert.True(vm.CanGoNext);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task StoppingAfterAProtocolChangeRebuildsTheChannelOptions(bool readingArrives) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            await fixture.PrepareLiveAsync(["nT", "nT", "nT"], ["X", "Y", "Z"]);
            vm.StartCollectingCommand.Execute(null);
            Assert.True(vm.IsCollecting, vm.CollectionStatus);
            fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [1, 2, 3] });

            // 采集中重连了轴顺序不同的协议：采集中不刷新选项。
            await fixture.PrepareLiveAsync(["nT", "nT", "nT"], ["Bz", "By", "Bx"]);
            Assert.Equal("X (nT)", vm.Fitting.Options[0].Label);
            if (readingArrives)
                fixture.Bus.PublishReading(new MagnetometerReading { Timestamp = DateTime.UtcNow, ChannelValues = [4, 5, 6] });
            else
                vm.StopCollectingCommand.Execute(null);

            Assert.False(vm.IsCollecting);
            Assert.Equal(new[] { "Bz (nT)", "By (nT)", "Bx (nT)" }, vm.Fitting.Options.Select(o => o.Label));
            Assert.Equal((2, 1, 0), (vm.Fitting.FitX1, vm.Fitting.FitY1, vm.Fitting.FitZ1));   // 按新名称重新识别
            Assert.Equal(new double[] { 1, 2, 3 }, Assert.Single(vm.CollectedData));
        });

    private static async Task<SessionInfo> SaveThreeReadingSessionAsync(Fixture fixture, string name)
    {
        string id = await fixture.Storage.StartSessionAsync(name, new SensorConfig
        {
            Type = SensorType.Generic, ChannelCountOverride = 3,
            ChannelNamesOverride = ["X", "Y", "Z"], ChannelUnitsOverride = ["nT", "nT", "nT"],
        }, new ConnectionConfig());
        await fixture.Storage.SaveReadingsAsync(Enumerable.Range(0, 3).Select(i => new MagnetometerReading
        { SessionId = id, Timestamp = DateTime.UtcNow.AddSeconds(i), ChannelValues = [i, i + 1, i + 2] }).ToList());
        await fixture.Storage.EndSessionAsync(id);
        return Assert.Single((await fixture.Storage.GetSessionsAsync()).Where(s => s.Id == id));
    }

    [Fact]
    public Task NullChannelNamesOrUnitsDoNotBreakTheConnectionCallback() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = await Fixture.CreateAsync();
            var vm = fixture.CreateVm(3);
            // 连接回调里建立拟合通道选项：名称或单位为 null 时不能抛异常（否则连接会被拆掉）。
            await fixture.PrepareLiveAsync([null!, "nT", "nT", "nT"], ["T", null!, "Y", "Z"]);
            Assert.Equal(new[] { "T", "通道 1 (nT)", "Y (nT)", "Z (nT)" }, vm.Fitting.Options.Select(o => o.Label));
            Assert.Equal((1, 2, 3), (vm.Fitting.FitX1, vm.Fitting.FitY1, vm.Fitting.FitZ1));   // 磁场通道恰好 3 个，按顺序
        });

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(5); !condition();)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待会话重新读取超时");
            await Task.Delay(10);
        }
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
        (vm.Fitting.FitX1, vm.Fitting.FitY1, vm.Fitting.FitZ1) = (0, 1, 2);
        if (channels == 6) (vm.Fitting.FitX2, vm.Fitting.FitY2, vm.Fitting.FitZ2) = (3, 4, 5);
    }

    private static List<double[]> SecondGroup(OrthogonalityCalibrationViewModel vm) => vm.SnapshotSecondGroupSamples();

    private static Task ImportSessionAsync(OrthogonalityCalibrationViewModel vm, SessionInfo session) => vm.LoadSessionDataAsync(session);

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"fitting-layout-{Guid.NewGuid():N}.db");
        /// <summary>原始 CSV 写到临时目录，不进入用户的 %LocalAppData%。</summary>
        public string RawDir { get; } = Path.Combine(Path.GetTempPath(), $"fitting-layout-raw-{Guid.NewGuid():N}");
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

        public SqliteCalibrationRepository CreateRepository() => Profiles;

        public OrthogonalityCalibrationViewModel CreateVm(int channels)
        {
            // Keep the real collection command and raw CSV writer, but never create a file
            // in the current user's calibration_raw directory during this regression.
            var vm = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), Profiles, Bus, Storage)
            {
                SelectedSensorType = channels == 6 ? SensorType.DualTriaxialFluxgate : SensorType.TriaxialFluxgate,
                RawDataDirectory = RawDir,
            };
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
            if (Directory.Exists(RawDir)) Directory.Delete(RawDir, recursive: true);
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
