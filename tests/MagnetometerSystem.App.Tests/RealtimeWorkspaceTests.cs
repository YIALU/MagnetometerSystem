using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.App.Tests;

public class RealtimeWorkspaceTests
{
    private static SensorConfig Configuration(int count = 4) => new()
    {
        Type = SensorType.Generic, SampleRate = 1234.5, ChannelCountOverride = count,
        ChannelNamesOverride = Enumerable.Range(0, count).Select(i => i == 3 ? "温度" : $"CH{i}").ToArray(),
        ChannelUnitsOverride = Enumerable.Range(0, count).Select(i => i == 3 ? "°C" : "nT").ToArray(),
    };

    private static void Publish(DataBus bus, DateTime start, int seconds, double first, bool corrected = false)
    {
        bus.PublishProcessedReading(new MagnetometerReading
        {
            Timestamp = start.AddSeconds(seconds), SensorType = SensorType.Generic,
            ChannelValues = [corrected ? first + 1000 : first, 20, 30, 26 + seconds * .001],
            OriginalChannelValues = corrected ? [first, 20, 30, 26 + seconds * .001] : null,
            IsOrthogonalityCorrected = corrected,
        });
    }

    [Fact]
    public Task SinglePlotUsesTemperatureAxisAndStatisticsStayRawAcrossPauseAndReorder() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { PlotControl = new ScottPlot.WPF.WpfPlot() };
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        var start = new DateTime(2020, 1, 1);
        for (int i = 0; i < 61; i++) Publish(bus, start, i, i, true);
        vm.ChannelConfigs.Move(3, 0);
        vm.RefreshPlot();
        var lines = vm.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().ToArray();
        Assert.Equal(4, lines.Length);
        var temperature = Assert.Single(lines.Where(p => p.LegendText.StartsWith("温度")));
        Assert.NotSame(vm.PlotControl.Plot.Axes.Left, temperature.Axes.YAxis);
        Assert.Equal("°C", temperature.Axes.YAxis.Label.Text);
        vm.IntervalStartInput = "0"; vm.IntervalEndInput = "60";
        vm.ApplyIntervalSelectionCommand.Execute(null);
        Assert.Equal(30, vm.IntervalStatistics!.ChannelStats[0].Mean, 8);
        vm.IsPaused = true;
        Publish(bus, start, 61, 61, true);
        vm.RefreshPlot();
        Assert.Equal(61, vm.DataPointCount);
        vm.IsPaused = false;
        vm.RefreshPlot();
        Assert.Equal(62, vm.DataPointCount);
        Assert.StartsWith("61 ", vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).LatestValue);
        vm.AutoOffsetChannelCommand.Execute(0);
        Assert.Equal(-1030.5, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset);
        Assert.Equal(0, vm.ChannelConfigs.Single(c => c.ChannelIndex == 3).DisplayOffset);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task DraggingOnPlotSelectsIntervalAndCrosshairShowsRawValues() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { PlotControl = new ScottPlot.WPF.WpfPlot() };
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        var start = new DateTime(2020, 1, 1);
        for (int i = 0; i < 61; i++) Publish(bus, start, i, i, corrected: true);
        vm.RefreshPlot();
        ScottPlot.Plottables.VerticalSpan[] Spans() => vm.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.VerticalSpan>().ToArray();

        vm.BeginPlotSelection(10.2);
        vm.UpdatePlotSelection(20.4);
        await WpfTestHost.PumpAsync(); // 拖动预览合并到界面空闲时重绘
        Assert.Single(Spans()); // 拖动中的预览
        Assert.True(vm.EndPlotSelection(20.4));
        Assert.Equal(10.2, vm.CurrentInterval!.StartTime, 9);
        Assert.Equal(20.4, vm.CurrentInterval.EndTime, 9);
        Assert.Equal(10, vm.IntervalStatistics!.SampleCount); // 11..20 秒
        Assert.Equal(15.5, vm.IntervalStatistics.ChannelStats[0].Mean, 9); // 原始值，不含校正后的 +1000
        Assert.Single(Spans());
        vm.RefreshPlot(); // 下一次刷新仍保留区间阴影
        Assert.Single(Spans());

        // 拖出数据范围时夹到已有数据；原地点击不产生区间。
        vm.BeginPlotSelection(55); Assert.True(vm.EndPlotSelection(500));
        Assert.Equal(60, vm.CurrentInterval!.EndTime, 9);
        vm.BeginPlotSelection(70); Assert.False(vm.EndPlotSelection(80));

        // 连续鼠标移动只触发一次叠加层重绘，显示最后的位置。
        var refreshes = vm.OverlayRefreshCount;
        for (int i = 0; i < 50; i++) vm.SetHoverTime(5 + i * 0.5);
        vm.SetHoverTime(30.4);
        Assert.Equal(refreshes, vm.OverlayRefreshCount);
        await WpfTestHost.PumpAsync();
        Assert.Equal(refreshes + 1, vm.OverlayRefreshCount);
        var readout = Assert.Single(vm.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Annotation>());
        Assert.StartsWith("30.000 s", readout.Text);
        Assert.Contains("CH0  30 nT", readout.Text);
        Assert.Contains("温度  26.03 °C", readout.Text);
        vm.SetHoverTime(null);
        await WpfTestHost.PumpAsync();
        Assert.Empty(vm.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Annotation>());

        vm.ClearIntervalSelectionCommand.Execute(null);
        Assert.Empty(Spans());
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task UnloadingChartViewForgetsOverlayPlots() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus);
        var view = new RealtimeChartView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 500, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();
            bus.PublishAcquisitionStarted(Configuration()); await WpfTestHost.PumpAsync();
            var start = new DateTime(2020, 1, 1);
            for (int i = 0; i < 20; i++) Publish(bus, start, i, i);
            vm.RefreshPlot();
            vm.SetHoverTime(5);
            await WpfTestHost.PumpAsync();
            Assert.Equal(1, vm.OverlayPlotCount);

            // 切页卸载视图后，视图模型不再留住旧图（及其曲线数据）。
            window.Content = null; await WpfTestHost.PumpAsync();
            Assert.Null(vm.PlotControl);
            Assert.Equal(0, vm.OverlayPlotCount);
        }
        finally
        {
            window.Close();
            bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
        }
    });

    [Fact]
    public Task LateChannelsAndShortFramesStayAlignedAndReconnectShrinksMetadata() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus(); using var vm = new RealtimeChartViewModel(bus);
        bus.PublishAcquisitionStarted(Configuration(1)); await WpfTestHost.PumpAsync();
        var t = DateTime.Now;
        Publish(bus, t, 0, 10);
        bus.PublishProcessedReading(new MagnetometerReading { Timestamp = t.AddSeconds(1), ChannelValues = [11] });
        Publish(bus, t, 2, 12);
        await WpfTestHost.PumpAsync(); vm.RefreshPlot();
        Assert.Equal(4, vm.ChannelConfigs.Count);
        Assert.Equal(3, vm.DataPointCount);
        bus.PublishAcquisitionStarted(Configuration(1)); await WpfTestHost.PumpAsync();
        bus.PublishProcessedReading(new MagnetometerReading { Timestamp = t, ChannelValues = [100] });
        vm.RefreshPlot(); Assert.Single(vm.ChannelConfigs); Assert.Equal(1, vm.DataPointCount);
        bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task NativeWorkspaceLoadsWithoutBindingErrorsAndFoldingReclaimsPlotSpace() => WpfTestHost.RunAsync(async () =>
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var bus = new DataBus(); using var storage = new SqliteStorageService(db, bus);
        var repo = new SqliteCalibrationRepository(db); var corrector = new OrthogonalityCorrector();
        var config = new AppConfigService(db);
        using var chart = new RealtimeChartViewModel(bus);
        var connection = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, repo);
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repo);
        using var history = new HistoryPlaybackViewModel(storage, bus, corrector, repo);
        var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), repo, bus, storage);
        var commands = new DeviceCommandViewModel(bus, config);
        var settings = new SettingsViewModel(config, new UpdateCoordinator(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }), new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db)));
        var analysis = new AnalysisViewModel(storage);
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, commands, bus, analysis);
        main.IsInitialized = true;
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 920, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var errors = new StringWriter(); var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();
            var view = FindVisualChild<AcquisitionWorkspaceView>(window);
            Assert.NotNull(view);
            var emptyState = (FrameworkElement)view.FindName("EmptyState");
            Assert.Equal(Visibility.Visible, emptyState.Visibility);
            bus.PublishAcquisitionStarted(Configuration()); await WpfTestHost.PumpAsync();
            var t = DateTime.Now; for (int i = 0; i < 80; i++) Publish(bus, t, i, Math.Sin(i * .12));
            chart.RefreshPlot(); window.UpdateLayout();
            Assert.NotNull(chart.PlotControl);
            // 未连接但曲线有数据（例如刚断开）：空状态不遮住曲线。
            Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
            // 默认侧栏和停靠区展开；专注后两者收起，曲线获得更多高度和宽度。
            Assert.True(main.WorkspaceLayout.SidePanelOpen && main.WorkspaceLayout.DockOpen);
            var dockedHeight = chart.PlotControl.ActualHeight;
            var dockedWidth = chart.PlotControl.ActualWidth;
            chart.FilterWindowSize = 17;
            main.WorkspaceLayout.ToggleFocusCommand.Execute(null);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.True(chart.PlotControl.ActualHeight > dockedHeight + 100);
            Assert.True(chart.PlotControl.ActualWidth > dockedWidth + 300);
            var screenshot = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
            if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, "workspace-focus.png"));
            main.WorkspaceLayout.ToggleFocusCommand.Execute(null);
            Assert.True(main.WorkspaceLayout.DockOpen);
            Assert.True(main.WorkspaceLayout.SidePanelOpen);
            Assert.Equal(17, chart.FilterWindowSize);
            chart.IsMultiPlotMode = true;
            chart.MultiPlotColumnCount = 2;
            await WpfTestHost.PumpAsync(); Assert.Equal(4, chart.MultiPlotControls.Count);
            chart.ComputedChannels.Add(new ComputedChannelDefinition { Name = "总场", Formula = "sqrt(CH0*CH0+CH1*CH1+CH2*CH2)" });
            Assert.Equal(5, chart.MultiPlotControls.Count);
            chart.ComputedChannels[0].Enabled = false;
            Assert.Equal(4, chart.MultiPlotControls.Count);
            chart.IsPaused = true;
            chart.MultiPlotColumnCount = 1;
            Assert.All(chart.MultiPlotControls, p => Assert.NotEmpty(p.Plot.GetPlottables()));
            chart.IsMultiPlotMode = false;
            Assert.Equal(4, chart.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().Count());
            chart.IsMultiPlotMode = true;
            chart.MultiPlotColumnCount = 2;
            if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, "workspace-expanded.png"));
            foreach (var tab in new[] { 0, 1, 2, 3, 4 }) { main.WorkspaceLayout.SideTab = tab; window.UpdateLayout(); await WpfTestHost.PumpAsync(); }
            foreach (var tab in new[] { 0, 1, 2 }) { main.WorkspaceLayout.DockTab = tab; window.UpdateLayout(); await WpfTestHost.PumpAsync(); }
            window.Height = 680; window.Width = 1100;
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.All(chart.MultiPlotControls, p => Assert.True(p.ActualHeight >= 140));
            chart.ClearChartCommand.Execute(null);
            Assert.All(chart.MultiPlotControls, p => Assert.Empty(p.Plot.GetPlottables()));
            Assert.Equal(Visibility.Visible, emptyState.Visibility);
            foreach (var page in new object[] { connection, sessions, commands, history, settings, analysis, ortho })
            {
                main.CurrentView = page;
                window.UpdateLayout(); await WpfTestHost.PumpAsync();
                if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, $"page-{main.CurrentPage}.png"));
            }
            // 校正页：三个页签与向导四步都渲染一遍（绑定错误会让本测试失败）。
            main.CurrentPage = AppPage.Calibration;
            foreach (var tab in new[] { CalibrationPage.SensorTab, CalibrationPage.LibraryTab, CalibrationPage.OrthoTab })
            {
                main.Calibration.SelectedTab = tab;
                window.UpdateLayout(); await WpfTestHost.PumpAsync();
                if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, $"page-Calibration-tab{tab}.png"));
            }
            ortho.SelectedSensorType = SensorType.DualTriaxialFluxgate;
            foreach (var step in new[] { 1, 2, 3, 4 })
            {
                ortho.CurrentStep = step;
                window.UpdateLayout(); await WpfTestHost.PumpAsync();
                if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, $"cal-step{step}.png"));
            }
            ortho.CurrentStep = 2;
            foreach (var source in new[] { CalibrationDataSource.File, CalibrationDataSource.Session, CalibrationDataSource.Live })
            { ortho.DataSource = source; window.UpdateLayout(); await WpfTestHost.PumpAsync(); }
            ortho.SelectedMode = CalibrationCollectionMode.Manual48;
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            ortho.SelectedMode = CalibrationCollectionMode.Continuous; ortho.CurrentStep = 1;
            // 旧的独立页面已合并进新导航。
            main.CurrentView = sessions; Assert.Same(main.DataPage, main.CurrentView); Assert.Equal(AppPage.Data, main.CurrentPage);
            main.CurrentView = ortho; Assert.Same(main.Calibration, main.CurrentView); Assert.Equal(AppPage.Calibration, main.CurrentPage);
            main.CurrentPage = AppPage.Workspace; Assert.Same(main, main.CurrentView);
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
        }
        finally
        {
            bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
            window.DataContext = null; window.Close(); ortho.Cleanup();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    });

    [Fact]
    public Task DisconnectKeepsLastCurveVisibleUntilChartIsCleared() => WpfTestHost.RunAsync(async () =>
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"workspace-empty-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var bus = new DataBus(); var storage = new SqliteStorageService(db, bus);
        var repo = new SqliteCalibrationRepository(db); var corrector = new OrthogonalityCorrector();
        var config = new AppConfigService(db);
        using var chart = new RealtimeChartViewModel(bus);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connection = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, repo)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ProtocolConfig = new ProtocolConfig
            {
                FieldMappings = Enumerable.Range(0, 3).Select(i => new FieldMapping { Name = $"CH{i}", Unit = "nT", ByteOffset = i, ChannelIndex = i }).ToList(),
            },
        };
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repo);
        using var history = new HistoryPlaybackViewModel(storage, bus, corrector, repo);
        var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), repo, bus, storage);
        var settings = new SettingsViewModel(config, new UpdateCoordinator(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }), new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db)));
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, new DeviceCommandViewModel(bus, config), bus);
        try
        {
            Assert.True(main.ShowWorkspaceEmptyState);
            var accept = listener.AcceptTcpClientAsync();
            await connection.ConnectCommand.ExecuteAsync(null);
            Assert.True(connection.IsConnected, connection.LastError);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(LinkState.Acquiring, main.LinkState);
            Assert.False(main.ShowWorkspaceEmptyState);
            await peer.GetStream().WriteAsync("1,2,3\n4,5,6\n7,8,9\n"u8.ToArray());
            for (var deadline = DateTime.UtcNow.AddSeconds(3); connection.ParsedReadingCount < 3 && DateTime.UtcNow < deadline;)
                await Task.Delay(20);
            Assert.Equal(3, connection.ParsedReadingCount);
            await connection.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();
            // 断开后曲线保留，空状态不能盖住刚采到的数据。
            Assert.Equal(LinkState.Idle, main.LinkState);
            Assert.Equal(3, chart.DataPointCount);
            Assert.False(main.ShowWorkspaceEmptyState);
            chart.ClearChartCommand.Execute(null);
            Assert.True(main.ShowWorkspaceEmptyState);
        }
        finally
        {
            await connection.StopAcquisitionAsync();
            listener.Stop(); ortho.Cleanup(); storage.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + suffix);
        }
    });

    private static byte[] XorFrame(float x, float y, float z)
    {
        var f = new byte[16];
        f[0] = 0xAA; f[1] = 0x55;
        BitConverter.GetBytes(x).CopyTo(f, 2);
        BitConverter.GetBytes(y).CopyTo(f, 6);
        BitConverter.GetBytes(z).CopyTo(f, 10);
        byte c = 0;
        for (int i = 0; i < 14; i++) c ^= f[i];
        f[14] = c; f[15] = 0x0D;
        return f;
    }

    [Fact]
    public Task RawFramesDockShowsPerFrameResultsOverRealTcp() => WpfTestHost.RunAsync(async () =>
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"workspace-records-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var bus = new DataBus(); var storage = new SqliteStorageService(db, bus);
        var repo = new SqliteCalibrationRepository(db); var corrector = new OrthogonalityCorrector();
        var config = new AppConfigService(db);
        using var chart = new RealtimeChartViewModel(bus);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var protocol = new ProtocolConfig
        {
            Name = "XOR 三轴", Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.DataField, Name = "X", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Z", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
                new() { Type = SegmentType.Checksum, Name = "校验", ByteCount = 1, ChecksumAlgorithm = ChecksumAlgorithm.Xor },
                new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        protocol.ComputeSegmentOffsets();
        var connection = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, repo)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, ProtocolConfig = protocol,
        };
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repo);
        using var history = new HistoryPlaybackViewModel(storage, bus, corrector, repo);
        var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), repo, bus, storage);
        var settings = new SettingsViewModel(config, new UpdateCoordinator(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }), new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db)));
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, new DeviceCommandViewModel(bus, config), bus);
        main.WorkspaceLayout.ShowDock(WorkspaceLayoutViewModel.DockRawFrames);
        var window = new Window
        {
            Content = new AcquisitionWorkspaceView { DataContext = main }, Width = 1280, Height = 860,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        var errors = new StringWriter(); var trace = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            window.Show();
            var accept = listener.AcceptTcpClientAsync();
            await connection.ConnectCommand.ExecuteAsync(null);
            Assert.True(connection.IsConnected, connection.LastError);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var bad = XorFrame(4, 5, 6); bad[14] ^= 0xFF;
            var stream = new byte[] { 0x3F, 0x0D }.Concat(XorFrame(1, 2, 3)).Concat(XorFrame(1, 2, 4)).Concat(bad).Concat(XorFrame(7, 8, 9)).ToArray();
            await peer.GetStream().WriteAsync(stream.AsMemory(0, 25));
            await peer.GetStream().WriteAsync(stream.AsMemory(25));
            for (var deadline = DateTime.UtcNow.AddSeconds(3); connection.ParsedReadingCount < 3 && DateTime.UtcNow < deadline;)
                await Task.Delay(20);
            Assert.Equal(3, connection.ParsedReadingCount);
            await connection.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();

            var rows = connection.ParseRecords.ToList();
            Assert.Equal([ParseOutcome.Skipped, ParseOutcome.Accepted, ParseOutcome.Rejected, ParseOutcome.Skipped, ParseOutcome.Accepted],
                rows.Select(r => r.Record.Outcome));
            Assert.Equal(2, rows[1].Record.FrameCount);
            Assert.StartsWith("✓ 连续 2 帧通过", rows[1].StatusText);
            Assert.Contains("XOR 校验失败", rows[2].StatusText);
            Assert.EndsWith("已重新同步", rows[2].StatusText);
            Assert.Equal("err", rows[2].Level);
            Assert.StartsWith("AA 55", rows[2].Record.Preview);
            Assert.True(connection.ParseErrorCount >= 1);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            // 同一批数据也出现在“收发”中（接收行，未发送命令时没有结论）。
            for (var deadline = DateTime.UtcNow.AddSeconds(3); main.DeviceCommandVM.TrafficEntries.Count < 2 && DateTime.UtcNow < deadline;)
                await Task.Delay(20);
            Assert.All(main.DeviceCommandVM.TrafficEntries, e => Assert.Equal(TrafficKind.Rx, e.Kind));
            Assert.Equal(stream.Length, main.DeviceCommandVM.TrafficEntries.Sum(e => e.ByteCount));
            main.WorkspaceLayout.DockTab = WorkspaceLayoutViewModel.DockTraffic;
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());

            // 链路条“异常 N”：即使之前切到“数据块”，也打开原始报文的解析记录。
            connection.ShowRawBlocks = true;
            main.WorkspaceLayout.DockTab = WorkspaceLayoutViewModel.DockTraffic;
            main.ShowRawFramesCommand.Execute(null);
            Assert.Equal(AppPage.Workspace, main.CurrentPage);
            Assert.Equal(WorkspaceLayoutViewModel.DockRawFrames, main.WorkspaceLayout.DockTab);
            Assert.False(connection.ShowRawBlocks);

            connection.ClearRawDataCommand.Execute(null);
            Assert.Empty(connection.ParseRecords);
        }
        finally
        {
            await connection.StopAcquisitionAsync();
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            listener.Stop(); ortho.Cleanup(); storage.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + suffix);
        }
    });

    [Fact]
    public Task AllProtocolChannelsRemainAvailableAndMagneticWizardsPreserveUnits() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus(); using var vm = new RealtimeChartViewModel(bus);
        var config = Configuration(65);
        config.ChannelUnitsOverride = Enumerable.Repeat("uT", 65).ToArray();
        config.ChannelUnitsOverride[3] = "°C";
        bus.PublishAcquisitionStarted(config); await WpfTestHost.PumpAsync();
        Assert.Equal(65, vm.ChannelConfigs.Count);
        vm.StartAddTotalFieldCommand.Execute(null);
        vm.WizardSourceA = 0; vm.WizardSourceB = 1; vm.WizardSourceC = 3;
        vm.ConfirmAddTotalFieldCommand.Execute(null); Assert.Empty(vm.ComputedChannels);
        vm.WizardSourceC = 2; vm.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Equal("uT", Assert.Single(vm.ComputedChannels).Unit);
        vm.StartAddGradientCommand.Execute(null);
        vm.WizardSourceA = 0; vm.WizardSourceB = 1;
        vm.ConfirmAddGradientCommand.Execute(null);
        Assert.Equal("uT/m", vm.ComputedChannels[1].Unit);
        bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task MultiPlotKeepsNameUnitAndDataTogetherAfterReorder() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus);
        var view = new RealtimeChartView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();
            bus.PublishAcquisitionStarted(Configuration()); await WpfTestHost.PumpAsync();
            vm.StopRenderTimer();
            var start = new DateTime(2020, 1, 1);
            for (int i = 0; i < 5; i++)
                bus.PublishProcessedReading(new MagnetometerReading
                {
                    Timestamp = start.AddSeconds(i), ChannelValues = [100 + i, 200 + i, 300 + i, 25 + i],
                });
            vm.IsMultiPlotMode = true;
            // 拖动排序把温度移到最前，再隐藏 CH1：视图按显示顺序建图，每张图的名称、单位和数据仍是同一个通道。
            vm.ReorderChannels(3, 0);
            vm.ChannelConfigs.Single(c => c.ChannelIndex == 1).Visible = false;
            vm.RefreshPlot();

            var grid = Assert.IsType<Grid>(Assert.Single(((Panel)view.FindName("MultiPlotPanel")).Children));
            var plots = grid.Children.OfType<ScottPlot.WPF.WpfPlot>().OrderBy(Grid.GetRow).ToArray();
            (string Label, double Base)[] expected = [("温度 (°C)", 25), ("CH0 (nT)", 100), ("CH2 (nT)", 300)];
            Assert.Equal(expected.Length, plots.Length);
            for (int p = 0; p < plots.Length; p++)
            {
                Assert.Equal(expected[p].Label, plots[p].Plot.Axes.Left.Label.Text);
                var line = Assert.Single(plots[p].Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>());
                var points = line.Data.GetScatterPoints();
                Assert.Equal([0d, 1, 2, 3, 4], points.Select(point => point.X));
                Assert.Equal(Enumerable.Range(0, 5).Select(i => expected[p].Base + i), points.Select(point => point.Y));
            }
        }
        finally
        {
            window.Close();
            bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
        }
    });

    [Fact]
    public Task IntervalExportWritesRawValuesWithQuotedNamesAndRoundTripPrecision() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus);
        bus.PublishAcquisitionStarted(new SensorConfig
        {
            Type = SensorType.Generic, ChannelCountOverride = 2,
            ChannelNamesOverride = ["Bx, \"探头 A\"", "温度"], ChannelUnitsOverride = ["nT", "°C"],
        });
        await WpfTestHost.PumpAsync();
        vm.StopRenderTimer();
        var start = new DateTime(2020, 1, 1);
        for (int i = 0; i <= 10; i++)
            bus.PublishProcessedReading(new MagnetometerReading
            {
                Timestamp = start.AddSeconds(i), ChannelValues = [1000 + i, 20 + i],
                OriginalChannelValues = [i / 3.0, 25.125 + i], IsOrthogonalityCorrected = true,
            });
        vm.IntervalStartInput = "2"; vm.IntervalEndInput = "4";
        vm.ApplyIntervalSelectionCommand.Execute(null);
        var path = Path.Combine(Path.GetTempPath(), $"interval-{Guid.NewGuid():N}.csv");
        try
        {
            await vm.ExportIntervalFromBuffersAsync(path);
            // 导出校正前的原始值；R 格式能读回同一个 double；含逗号、引号的通道名按 CSV 规则加引号并转义。
            Assert.Equal(
            [
                "ElapsedSeconds,\"Bx, \"\"探头 A\"\" (nT)\",\"温度 (°C)\"",
                "2,0.6666666666666666,27.125",
                "3,1,28.125",
                "4,1.3333333333333333,29.125",
            ], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
            bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
        }
    });

    [Fact]
    public Task ChannelOrderIsRestoredOnNextStartOnlyForTheSameChannelCount() => WpfTestHost.RunAsync(async () =>
    {
        var preferences = new MemoryPreferences();
        // 每次用新的视图模型开始采集，相当于重新打开程序；返回开始后（及可选操作后）的通道顺序。
        async Task<int[]> StartAsync(SensorConfig config, Action<RealtimeChartViewModel>? change = null)
        {
            var bus = new DataBus();
            using var vm = new RealtimeChartViewModel(bus, preferences);
            bus.PublishAcquisitionStarted(config); await WpfTestHost.PumpAsync();
            change?.Invoke(vm);
            var order = vm.ChannelConfigs.Select(c => c.ChannelIndex).ToArray();
            bus.PublishAcquisitionStopped(); await WpfTestHost.PumpAsync();
            return order;
        }

        Assert.Equal([3, 0, 1, 2], await StartAsync(Configuration(), vm => vm.ReorderChannels(3, 0)));
        Assert.Equal([3, 0, 1, 2], preferences.Get<int[]>("ChartOrder"));
        Assert.Equal([3, 0, 1, 2], await StartAsync(Configuration()));
        // 通道数变了，保存的顺序不再适用，按协议顺序显示。
        Assert.Equal([0, 1, 2, 3, 4], await StartAsync(Configuration(5)));
    });

    /// <summary>内存中的偏好设置，按 JSON 保存，读回的是新对象。</summary>
    private sealed class MemoryPreferences : IUserPreferencesService
    {
        private readonly Dictionary<string, string> _json = new();

        public T? Get<T>(string key) =>
            _json.TryGetValue(key, out var json) ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : default;

        public Task<T?> GetPreferenceAsync<T>(string key) => Task.FromResult(Get<T>(key));

        public Task SetPreferenceAsync<T>(string key, T value)
        {
            _json[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisualChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
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
}
