using System.Globalization;
using System.IO;
using System.Reflection;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Charting;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using ScottPlot.WPF;

namespace MagnetometerSystem.App.Tests;

public class UnitWorkflowTests
{
    [Fact]
    public Task RecordedSessionUnits_ReachReplayAndChartEvenAfterSelectionChanges() => WpfTestHost.RunAsync(async () =>
    {
        var db = new DatabaseInitializer(Path.Combine(Path.GetTempPath(), $"replay-units-{Guid.NewGuid():N}.db"));
        await db.InitializeAsync();
        var bus = new DataBus();
        using var storage = new SqliteStorageService(db, bus);
        string id = await storage.StartSessionAsync("mixed", new SensorConfig
        {
            SampleRate = 1, ChannelCountOverride = 3, ChannelNamesOverride = ["B", "GPS", "T"],
            ChannelUnitsOverride = ["nT", "°", "°C"],
        }, new());
        await storage.SaveReadingsAsync([new() { SessionId = id, Timestamp = DateTime.Now, ChannelValues = [100, 30, 25] }]);
        await storage.EndSessionAsync(id);
        using var chart = new RealtimeChartViewModel(bus);
        using var renderer = new ChartRenderer(new WpfPlot());
        renderer.Attach(chart);
        using var replay = new HistoryPlaybackViewModel(storage, bus, new OrthogonalityCorrector(), new SqliteCalibrationRepository(db));
        replay.SelectedSession = Assert.Single(await storage.GetSessionsAsync());
        await replay.LoadSessionCommand.ExecuteAsync(null);
        // Changing the selector must not relabel the data already loaded into the player.
        replay.SelectedSession = new SessionInfo { ChannelCount = 1, ChannelUnits = ["m"], SampleRate = 1000 };
        await replay.PlayCommand.ExecuteAsync(null);
        await WpfTestHost.PumpAsync();
        Assert.Equal(new[] { "nT", "°", "°C" }, chart.ChannelConfigs.OrderBy(c => c.ChannelIndex).Select(c => c.Unit));
        Assert.Equal(new[] { "B", "GPS", "T" }, chart.ChannelConfigs.OrderBy(c => c.ChannelIndex).Select(c => c.Name));
        replay.StopCommand.Execute(null);
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task WizardsRejectIncompatibleSourcesAndDeriveUnitsWhileCustomUnitRemainsEditable() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var chart = new RealtimeChartViewModel(bus);
        using var renderer = new ChartRenderer(new WpfPlot());
        renderer.Attach(chart);
        bus.PublishAcquisitionStarted(new SensorConfig
        {
            ChannelCountOverride = 7,
            ChannelNamesOverride = Enumerable.Range(0, 7).Select(i => $"CH{i}").ToArray(),
            ChannelUnitsOverride = ["nT", "nT", "nT", "°C", "µT", "µT", "µT"],
        });
        await WpfTestHost.PumpAsync();
        var wizard = chart.Wizard;
        wizard.StartAddTotalFieldCommand.Execute(null);
        wizard.WizardSourceA = 0; wizard.WizardSourceB = 1; wizard.WizardSourceC = 3;
        wizard.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Empty(chart.ComputedChannels);
        Assert.Contains("相同的磁场单位", wizard.ComputationError);
        wizard.WizardSourceC = 2;
        wizard.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Equal("nT", Assert.Single(chart.ComputedChannels).Unit);
        wizard.StartAddTotalFieldCommand.Execute(null);
        wizard.WizardSourceA = 4; wizard.WizardSourceB = 5; wizard.WizardSourceC = 6;
        wizard.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Equal("µT", chart.ComputedChannels[1].Unit);
        wizard.StartAddGradientCommand.Execute(null);
        wizard.WizardSourceA = 0; wizard.WizardSourceB = 3;
        wizard.ConfirmAddGradientCommand.Execute(null);
        Assert.Equal(2, chart.ComputedChannels.Count);
        wizard.WizardSourceB = 1;
        wizard.GradientBaselineDistance = 0;
        wizard.ConfirmAddGradientCommand.Execute(null);
        Assert.Equal(2, chart.ComputedChannels.Count);
        Assert.Contains("有限正数", wizard.ComputationError);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            wizard.GradientBaselineDistance = 0.5;
            wizard.ConfirmAddGradientCommand.Execute(null);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        Assert.Equal("nT/m", chart.ComputedChannels[2].Unit);
        Assert.Contains("0.5", chart.ComputedChannels[2].Formula);
        chart.AddComputedChannelCommand.Execute(null);
        var custom = chart.ComputedChannels[^1];
        Assert.Equal("CH0", custom.Formula);
        Assert.Empty(custom.Unit);
        bool notified = false;
        custom.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(custom.Unit);
        custom.Formula = "CH3";
        custom.Unit = "°C";
        Assert.True(notified);
        var start = DateTime.Now;
        for (int i = 0; i < 3; i++)
            bus.PublishProcessedReading(new MagnetometerReading { Timestamp = start.AddMilliseconds(i * 10), ChannelValues = [100, 200, 300, 25, 1, 2, 3] });
        chart.RefreshPlot();
        var lines = renderer.SinglePlot.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().ToArray();
        Assert.Equal(11, lines.Length);
        Assert.Equal("°C", lines[^1].Axes.YAxis.Label.Text);
        Assert.Same(lines[3].Axes.YAxis, lines[^1].Axes.YAxis);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });
}
