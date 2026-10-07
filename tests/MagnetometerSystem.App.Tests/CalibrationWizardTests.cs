using System.Diagnostics;
using System.IO;
using System.Windows;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class CalibrationWizardTests
{
    /// <summary>球面上均匀分布的方向，经已知偏移和非正交畸变后作为“原始”读数。</summary>
    private static double[] DistortedSample(int i, int count, double radius = 50000)
    {
        double z = 1 - 2.0 * (i + .5) / count, r = Math.Sqrt(1 - z * z), phi = i * Math.PI * (3 - Math.Sqrt(5));
        double x = r * Math.Cos(phi) * radius, y = r * Math.Sin(phi) * radius;
        z *= radius;
        return [1.02 * x + .01 * y + 120, .015 * x + .98 * y - 80, -.01 * y + 1.01 * z + 35];
    }

    private static MagnetometerReading Reading(double[] values) => new() { Timestamp = DateTime.Now, ChannelValues = values };

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"calib_{Guid.NewGuid():N}.db");
        public DataBus Bus { get; } = new();
        public DatabaseInitializer Database { get; private set; } = null!;
        public SqliteStorageService Storage { get; private set; } = null!;
        public OrthogonalityCalibrationViewModel Ortho { get; private set; } = null!;
        public SqliteCalibrationRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Database = new DatabaseInitializer(f._path);
            await f.Database.InitializeAsync();
            f.Storage = new SqliteStorageService(f.Database, f.Bus);
            f.Repository = new SqliteCalibrationRepository(f.Database);
            f.Ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), f.Repository, f.Bus, f.Storage);
            return f;
        }

        /// <summary>与真实采集一致：先发布本次采集的通道单位，再出现当前连接。拟合采集只在这之后开始。</summary>
        public async Task PrepareLiveAsync(int channels)
        {
            await Bus.PublishAcquisitionStartingAsync(new SensorConfig
            {
                Type = SensorType.Generic, SampleRate = 10, ChannelCountOverride = channels,
                ChannelNamesOverride = Enumerable.Range(0, channels).Select(i => $"B{i}").ToArray(),
                ChannelUnitsOverride = Enumerable.Repeat("nT", channels).ToArray(),
            });
            Bus.PublishConnectionChanged(new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = 9, AutoReconnect = false }));
        }

        public void Dispose()
        {
            Bus.PublishConnectionChanged(null);
            Ortho.Cleanup();
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        }
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ready(), "等待界面状态更新超时");
    }

    [Fact]
    public Task ContinuousCollection_CalculatesSavesAndShowsInLibrary_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        var page = new CalibrationPage(vm, new SensorCalibrationViewModel(f.Repository));
        var window = new Window
        {
            Content = new CalibrationPageView { DataContext = page }, Width = 1280, Height = 860,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        var errors = new StringWriter();
        using var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            window.Show();
            await vm.EnsureLoadedAsync();
            Assert.True(vm.CanGoNext); // 默认已选单三轴
            vm.NextStepCommand.Execute(null);
            Assert.Equal(2, vm.CurrentStep);

            vm.StartCollectingCommand.Execute(null);
            Assert.False(vm.IsCollecting); // 未连接时不能开始拟合采集
            Assert.Contains("请先连接设备", vm.CollectionStatus);
            await f.PrepareLiveAsync(3);
            vm.StartCollectingCommand.Execute(null);
            Assert.True(vm.IsCollecting, vm.CollectionStatus);
            Assert.Equal("nT", vm.CollectedUnit);
            Assert.False(vm.CanGoNext);
            Assert.Contains("先结束采集", vm.StepGateText);
            const int count = 240;
            // 从后台线程发布，与真实接收线程一致。
            await Task.Run(() => { for (int i = 0; i < count; i++) f.Bus.PublishReading(Reading(DistortedSample(i, count))); });
            await WaitForAsync(() => vm.CollectedSampleCount == count);
            Assert.Equal(count, vm.CollectedData.Count);
            vm.StopCollectingCommand.Execute(null);
            Assert.True(vm.SphericityCoverage > 80);
            Assert.NotNull(vm.DataValidation);
            Assert.True(vm.CanGoNext);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();

            vm.NextStepCommand.Execute(null);
            await vm.RunCalculationCommand.ExecuteAsync(null);
            var result = Assert.IsType<OrthoGroupResult>(vm.DisplayedResult);
            Assert.Equal("优秀", result.Rating);
            Assert.Equal("ok", result.Level);
            Assert.Equal(9, result.Matrix.Length);
            Assert.Equal(count, result.Quality.SampleCount);
            Assert.True(result.Quality.ResidualStd < 1, $"残差标准差 {result.Quality.ResidualStd}");
            Assert.True(vm.CanGoNext);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();

            vm.NextStepCommand.Execute(null);
            vm.ProfileName = "测试探头";
            await vm.SaveProfileCommand.ExecuteAsync(null);
            Assert.Contains("配置库", vm.SaveStatus);
            Assert.DoesNotContain("应用", vm.SaveStatus);
            var saved = Assert.Single(vm.SavedProfiles);
            Assert.Equal("测试探头", saved.Name);
            Assert.Equal(count, saved.SampleCount);

            page.SelectedTab = CalibrationPage.LibraryTab;
            vm.SelectedSavedProfile = saved;
            Assert.True(vm.ExportSelectedProfileCsvCommand.CanExecute(null));
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    });

    [Fact]
    public Task ManualPoints_RecordUndoClearAndLinkBarRequest() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.SelectedSensorType = SensorType.DualTriaxialFluxgate;
        vm.CurrentStep = 2;
        vm.SelectedMode = CalibrationCollectionMode.Manual48;
        await f.PrepareLiveAsync(6);
        vm.StartCollectingCommand.Execute(null);
        Assert.True(f.Bus.ManualOrthoState.IsActive);
        Assert.Equal("next", vm.ManualPointSlots[0]);

        vm.RecordCurrentPointCommand.Execute(null);
        Assert.Equal(0, vm.CollectedSampleCount); // 还没有读数
        // 每点是最近 10 条读数的均值：不足 10 条时页面、链路条两个入口都不记录。
        for (int i = 0; i < 5; i++) f.Bus.PublishReading(Reading([1, 2, 3, 4, 5, 6]));
        vm.RecordCurrentPointCommand.Execute(null);
        f.Bus.RaiseManualOrthoRecord();
        Assert.Equal(0, vm.CollectedSampleCount);
        Assert.Contains("5/10", vm.CollectionStatus);
        Assert.False(vm.ManualState.HasEnoughBuffer);
        for (int i = 0; i < 10; i++) f.Bus.PublishReading(Reading([1, 2, 3, 4, 5, 6 + i]));
        vm.RecordCurrentPointCommand.Execute(null);
        f.Bus.RaiseManualOrthoRecord(); // 链路条上的“记录当前点”
        f.Bus.RaiseManualOrthoRecord();
        Assert.Equal(3, vm.CollectedSampleCount);
        Assert.Equal(3, f.Bus.ManualOrthoState.PointsRecorded);
        Assert.Equal(["on", "on", "on", "next"], vm.ManualPointSlots.Take(4));
        Assert.Equal([1d, 2, 3], vm.CollectedData[0]);

        vm.UndoLastPointCommand.Execute(null);
        Assert.Equal(2, vm.CollectedSampleCount);
        Assert.Equal(2, vm.CollectedData.Count);
        vm.ClearManualPointsCommand.Execute(null);
        Assert.Equal(0, vm.CollectedSampleCount);
        Assert.False(vm.UndoLastPointCommand.CanExecute(null));

        for (int i = 0; i < 3; i++) vm.RecordCurrentPointCommand.Execute(null);
        vm.StopCollectingCommand.Execute(null);
        Assert.False(f.Bus.ManualOrthoState.IsActive);
        Assert.Equal(3, vm.CollectedSampleCount);
        // 停止后链路条的请求不再记录。
        f.Bus.RaiseManualOrthoRecord();
        Assert.Equal(3, vm.CollectedSampleCount);
        await WpfTestHost.PumpAsync();
    });

    /// <summary>
    /// 接收端在自己的锁内回调 ReadingReceived，界面线程刷新接收计数时也要拿这把锁。
    /// 校正采集若在回调里同步等待界面线程，两边会互相等待。
    /// </summary>
    [Fact]
    public Task ContinuousCollection_NeverBlocksReceiveThreadOnUi() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.CurrentStep = 2;
        await f.PrepareLiveAsync(3);
        vm.StartCollectingCommand.Execute(null);
        Assert.True(vm.IsCollecting, vm.CollectionStatus);
        var receiveGate = new object();
        const int count = 400;
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
                lock (receiveGate) f.Bus.PublishReading(Reading(DistortedSample(i, count)));
        });
        while (!producer.IsCompleted)
        {
            lock (receiveGate) { }
            await WpfTestHost.PumpAsync();
        }
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => vm.CollectedSampleCount == count);
        vm.StopCollectingCommand.Execute(null);
    });
}
