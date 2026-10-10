using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        /// <summary>原始 CSV 写到临时目录，不进入用户的 %LocalAppData%。</summary>
        public string RawDir { get; } = Path.Combine(Path.GetTempPath(), $"calib_raw_{Guid.NewGuid():N}");
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
            f.Ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), f.Repository, f.Bus, f.Storage)
            {
                RawDataDirectory = f.RawDir,
            };
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
            if (Directory.Exists(RawDir)) Directory.Delete(RawDir, recursive: true);
        }
    }

    private static void SaveScreenshot(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ready(), "等待界面状态更新超时");
    }

    private static void AssertSamples(IReadOnlyList<double[]> actual, params double[][] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
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
        var shots = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
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
            // 开始采集即显示原始 CSV 的位置与文件名，可以打开。
            var rawPath = Assert.IsType<string>(vm.RawFilePath);
            Assert.Equal(f.RawDir, vm.RawFileDirectory);
            Assert.Equal(Path.GetFileName(rawPath), vm.RawFileName);
            Assert.EndsWith("_raw.csv", vm.RawFileName);
            Assert.True(File.Exists(rawPath));
            Assert.True(vm.OpenRawFileCommand.CanExecute(null));
            Assert.True(vm.OpenRawFileFolderCommand.CanExecute(null));
            Assert.Equal("写入中 · 0 行", vm.RawFileStatus);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            const int count = 240;
            // 从后台线程发布，与真实接收线程一致。
            await Task.Run(() => { for (int i = 0; i < count; i++) f.Bus.PublishReading(Reading(DistortedSample(i, count))); });
            await WaitForAsync(() => vm.CollectedSampleCount == count);
            Assert.Equal(count, vm.CollectedData.Count);
            Assert.Equal($"写入中 · {count} 行", vm.RawFileStatus);
            await WpfTestHost.PumpAsync();
            if (!string.IsNullOrEmpty(shots)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(shots, "calib-step2-collecting.png"));
            vm.StopCollectingCommand.Execute(null);
            Assert.Equal($"采集已结束 · 共 {count} 行", vm.RawFileStatus);
            await WpfTestHost.PumpAsync();
            if (!string.IsNullOrEmpty(shots)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(shots, "calib-step2-stopped.png"));
            Assert.False(vm.RawFileFailed);
            var rawLines = File.ReadAllLines(rawPath);
            Assert.Equal(count, rawLines.Count(l => l.Length > 0 && char.IsDigit(l[0])));
            Assert.StartsWith($"{count},", rawLines[^1]);
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
        Assert.Equal(vm.RawFilePath, f.Bus.ManualOrthoState.RawFilePath);
        vm.StopCollectingCommand.Execute(null);
        Assert.False(f.Bus.ManualOrthoState.IsActive);
        Assert.Equal(3, vm.CollectedSampleCount);
        // 原始 CSV 只追加：撤销、清空前写入的 3 行仍在，加上之后的 3 行。
        Assert.Equal("采集已结束 · 共 6 行", vm.RawFileStatus);
        // 退订前已在进行的读数回调在停止之后才执行：不能把链路条状态改回“采集中”。
        vm.OnCalibrationDataReceived(Reading([1, 2, 3, 4, 5, 6]));
        Assert.False(f.Bus.ManualOrthoState.IsActive);
        // 停止后链路条的请求不再记录。
        f.Bus.RaiseManualOrthoRecord();
        Assert.Equal(3, vm.CollectedSampleCount);
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task ManualDualPoints_KeepGroupsAlignedAndRawCsvRecordsUndoAndClear() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.SelectedSensorType = SensorType.DualTriaxialFluxgate;
        vm.CurrentStep = 2;
        vm.SelectedMode = CalibrationCollectionMode.Manual48;
        await f.PrepareLiveAsync(6);
        vm.StartCollectingCommand.Execute(null);
        Assert.True(vm.IsCollecting, vm.CollectionStatus);
        var rawPath = Assert.IsType<string>(vm.RawFilePath);
        // 每个点是最近 10 条读数的均值：送满 10 条同一方位的读数再记录，第二组比第一组大 10。
        void RecordPoint(double v)
        {
            for (int i = 0; i < 10; i++) f.Bus.PublishReading(Reading([v, v + 1, v + 2, v + 10, v + 11, v + 12]));
            vm.RecordCurrentPointCommand.Execute(null);
        }

        foreach (var v in new double[] { 100, 200, 300 }) RecordPoint(v);
        AssertSamples(vm.CollectedData, [100, 101, 102], [200, 201, 202], [300, 301, 302]);
        AssertSamples(vm.SnapshotSecondGroupSamples(), [110, 111, 112], [210, 211, 212], [310, 311, 312]);
        // 撤销与清空同时作用于两组，第二组始终与第一组逐点对应。
        vm.UndoLastPointCommand.Execute(null);
        AssertSamples(vm.CollectedData, [100, 101, 102], [200, 201, 202]);
        AssertSamples(vm.SnapshotSecondGroupSamples(), [110, 111, 112], [210, 211, 212]);
        vm.ClearManualPointsCommand.Execute(null);
        Assert.Empty(vm.CollectedData);
        Assert.Empty(vm.SnapshotSecondGroupSamples());
        foreach (var v in new double[] { 400, 500 }) RecordPoint(v);
        AssertSamples(vm.CollectedData, [400, 401, 402], [500, 501, 502]);
        AssertSamples(vm.SnapshotSecondGroupSamples(), [410, 411, 412], [510, 511, 512]);
        vm.StopCollectingCommand.Execute(null);
        Assert.Equal("采集已结束 · 共 5 行", vm.RawFileStatus);

        var lines = File.ReadAllLines(rawPath);
        Assert.Contains("# Sensor Type         : DualTriaxialFluxgate", lines);
        Assert.Contains("# Unit                : nT", lines);
        Assert.Contains("# Collection Mode     : Manual48", lines);
        Assert.Contains("# Source Channels     : X1=B0, Y1=B1, Z1=B2, X2=B3, Y2=B4, Z2=B5", lines);
        var body = lines.SkipWhile(l => !l.StartsWith("point_index,")).ToArray();
        Assert.Equal("point_index,timestamp,X1,Y1,Z1,X2,Y2,Z2", body[0]);
        // 时间戳之外逐行核对。原始 CSV 只追加：撤销、清空写成注释行，已写的行不改，行号接着已写入的行数。
        static string WithoutTimestamp(string line)
        {
            if (line.StartsWith('#')) return "# " + line.Split(' ', 4)[3];
            var fields = line.Split(',');
            Assert.True(DateTime.TryParseExact(fields[1], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _), line);
            return string.Join(",", fields.Where((_, i) => i != 1));
        }
        Assert.Equal(
        [
            "1,100,101,102,110,111,112",
            "2,200,201,202,210,211,212",
            "3,300,301,302,310,311,312",
            "# 已撤销第 3 点",
            "# 已清空之前的 2 点，重新记录",
            "4,400,401,402,410,411,412",
            "5,500,501,502,510,511,512",
        ], body.Skip(1).Select(WithoutTimestamp));
    });

    [Fact]
    public Task RawFile_ExistingNameIsNotOverwritten_AndWriteFailureIsShownWithoutStoppingCollection() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.CurrentStep = 2;
        await f.PrepareLiveAsync(3);

        // 同一秒内已有同名文件（例如刚停止又开始）：另起新名，不覆盖已有的原始数据。
        Directory.CreateDirectory(f.RawDir);
        var now = DateTime.Now;
        var existing = Enumerable.Range(0, 3)
            .Select(s => Path.Combine(f.RawDir, $"{vm.ProfileName}_{now.AddSeconds(s):yyyyMMdd_HHmmss}_raw.csv")).ToArray();
        foreach (var path in existing) File.WriteAllText(path, "keep");
        vm.StartCollectingCommand.Execute(null);
        var second = Assert.IsType<string>(vm.RawFilePath);
        Assert.EndsWith("_raw_2.csv", second);
        Assert.True(File.Exists(second));
        Assert.All(existing, path => Assert.Equal("keep", File.ReadAllText(path)));

        // 写入中途失败（此处让写入器失效）：停止写这个文件并说明原因，拟合样本照常累积。
        for (int i = 0; i < 2; i++) f.Bus.PublishReading(Reading([1, 2, 3 + i]));
        await WaitForAsync(() => vm.CollectedSampleCount == 2);
        Assert.IsType<StreamWriter>(vm.CurrentRawWriter).Dispose();
        for (int i = 0; i < 3; i++) f.Bus.PublishReading(Reading([1, 2, 5 + i]));
        await WaitForAsync(() => vm.CollectedSampleCount == 5);
        Assert.True(vm.RawFileFailed);
        Assert.Contains("写入失败", vm.RawFileStatus);
        Assert.Contains("此前已写入 2 行", vm.RawFileStatus);
        vm.StopCollectingCommand.Execute(null);
        Assert.True(vm.RawFileFailed);
        Assert.Equal(5, vm.CollectedSampleCount);
        Assert.Equal(2, File.ReadAllLines(second).Count(l => l.Length > 0 && char.IsDigit(l[0])));
    });

    [Fact]
    public Task RawFile_CreateFailureIsShown_AndCollectionStillRuns() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.CurrentStep = 2;
        await f.PrepareLiveAsync(3);
        // 目录位置被同名文件占用，无法创建目录。
        Directory.CreateDirectory(f.RawDir);
        var blocked = Path.Combine(f.RawDir, "blocked");
        File.WriteAllText(blocked, "");
        vm.RawDataDirectory = blocked;

        vm.StartCollectingCommand.Execute(null);
        Assert.True(vm.IsCollecting, vm.CollectionStatus);
        Assert.True(vm.RawFileFailed);
        Assert.Contains("无法创建文件", vm.RawFileStatus);
        Assert.Equal(blocked, vm.RawFileDirectory);
        for (int i = 0; i < 3; i++) f.Bus.PublishReading(Reading([1, 2, 3 + i]));
        await WaitForAsync(() => vm.CollectedSampleCount == 3);
        vm.StopCollectingCommand.Execute(null);
        Assert.True(vm.RawFileFailed);
        Assert.Contains("本次采集的点不会写入文件", vm.RawFileStatus);
        Assert.DoesNotContain("。。", vm.RawFileStatus);
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

    [Fact]
    public Task CsvImport_MapsHeaderAliasesTimestampsAndDelimiters_AndReportsSkippedLines() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = f.Ortho;
        vm.CurrentStep = 2;
        vm.DataSource = CalibrationDataSource.File;
        Directory.CreateDirectory(f.RawDir);
        string Write(string name, params string[] lines)
        {
            var path = Path.Combine(f.RawDir, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        // 不猜测单位：没有明确选择单位时不读取文件。
        var aliases = Write("aliases.csv", "time,BZ,bx,by", "t0,3,1,2", "t1,6,4,5", "t2,oops,7,8", "", "t3,9,7,8");
        vm.ImportCsvFile(aliases);
        Assert.Contains("请先明确选择", vm.CollectionStatus);
        Assert.Equal(0, vm.CollectedSampleCount);

        // 表头按列名（不分大小写的别名）而不是列位置取 X、Y、Z；解析失败的行计入跳过，空行不计。
        vm.FittingUnit = "uT";
        vm.ImportCsvFile(aliases);
        Assert.Equal("已从文件导入 3 个样本（跳过 1 行）", vm.CollectionStatus);
        Assert.Equal("uT", vm.CollectedUnit);
        AssertSamples(vm.CollectedData, [1, 2, 3], [4, 5, 6], [7, 8, 9]);

        // 双三轴、分号分隔。两组各自拟合：第二组缺列的行只计入第一组。
        vm.SelectedSensorType = SensorType.DualTriaxialFluxgate;
        vm.ImportCsvFile(Write("dual.csv", "X1;Y1;Z1;X2;Y2;Z2", "1;2;3;4;5;6", "7;8;9;;;", "10;11;12;13;14;15"));
        Assert.Equal("已从文件导入 3 个样本", vm.CollectionStatus);
        AssertSamples(vm.CollectedData, [1, 2, 3], [7, 8, 9], [10, 11, 12]);
        AssertSamples(vm.SnapshotSecondGroupSamples(), [4, 5, 6], [13, 14, 15]);

        // 无表头、制表符分隔、第一列是时间戳：跳过时间戳列，数值按不变区域性解析（含指数）。
        vm.SelectedSensorType = SensorType.TriaxialFluxgate;
        vm.ImportCsvFile(Write("timestamps.txt", "2026-01-01 00:00:00.000\t1.5\t-2.5\t3e2", "2026-01-01 00:00:00.100\t4\t5\t6"));
        Assert.Equal("已从文件导入 2 个样本", vm.CollectionStatus);
        AssertSamples(vm.CollectedData, [1.5, -2.5, 300], [4, 5, 6]);

        // 没有一行有效：报告失败，保留之前导入的样本。
        vm.ImportCsvFile(Write("invalid.csv", "x,y,z", "1,2", "1,two,3"));
        Assert.StartsWith("导入失败：文件中未找到有效的三轴数据", vm.CollectionStatus);
        AssertSamples(vm.CollectedData, [1.5, -2.5, 300], [4, 5, 6]);
        Assert.Equal(2, vm.CollectedSampleCount);
    });
}
