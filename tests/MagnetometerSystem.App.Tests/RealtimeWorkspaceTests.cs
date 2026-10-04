using System.Diagnostics;
using System.IO;
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
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, commands, bus);
        main.IsInitialized = true;
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 920, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var errors = new StringWriter(); var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();
            var view = FindVisualChild<AcquisitionWorkspaceView>(window);
            Assert.NotNull(view);
            bus.PublishAcquisitionStarted(Configuration()); await WpfTestHost.PumpAsync();
            var t = DateTime.Now; for (int i = 0; i < 80; i++) Publish(bus, t, i, Math.Sin(i * .12));
            chart.RefreshPlot(); window.UpdateLayout();
            Assert.NotNull(chart.PlotControl);
            var fullHeight = chart.PlotControl.ActualHeight;
            main.WorkspaceLayout.ChannelsExpanded = true;
            main.WorkspaceLayout.TerminalExpanded = true;
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.True(chart.PlotControl.ActualHeight < fullHeight);
            chart.FilterWindowSize = 17;
            main.WorkspaceLayout.ToggleFocusCommand.Execute(null);
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.True(chart.PlotControl.ActualHeight >= fullHeight - 2);
            var screenshot = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
            if (!string.IsNullOrEmpty(screenshot)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(screenshot, "workspace-focus.png"));
            main.WorkspaceLayout.ToggleFocusCommand.Execute(null);
            Assert.True(main.WorkspaceLayout.TerminalExpanded);
            Assert.True(main.WorkspaceLayout.ChannelsExpanded);
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
            main.WorkspaceLayout.ConnectionExpanded = true;
            main.WorkspaceLayout.StorageExpanded = true;
            main.WorkspaceLayout.AnalysisExpanded = true;
            window.Height = 650; window.Width = 1000;
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.All(chart.MultiPlotControls, p => Assert.True(p.ActualHeight >= 140));
            chart.ClearChartCommand.Execute(null);
            Assert.All(chart.MultiPlotControls, p => Assert.Empty(p.Plot.GetPlottables()));
            foreach (var page in new object[] { connection, sessions, commands, history, settings })
            {
                main.CurrentView = page;
                window.UpdateLayout(); await WpfTestHost.PumpAsync();
            }
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
