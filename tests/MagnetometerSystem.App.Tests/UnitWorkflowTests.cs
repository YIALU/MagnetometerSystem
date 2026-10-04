using System.Globalization;
using System.IO;
using System.Reflection;
using MagnetometerSystem.App.ViewModels;
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
        using var chart = new RealtimeChartViewModel(bus) { PlotControl = new WpfPlot() };
        var replay = new HistoryPlaybackViewModel(storage, bus, new OrthogonalityCorrector(), new SqliteCalibrationRepository(db));
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
        using var chart = new RealtimeChartViewModel(bus) { PlotControl = new WpfPlot() };
        bus.PublishAcquisitionStarted(new SensorConfig
        {
            ChannelCountOverride = 7,
            ChannelNamesOverride = Enumerable.Range(0, 7).Select(i => $"CH{i}").ToArray(),
            ChannelUnitsOverride = ["nT", "nT", "nT", "°C", "µT", "µT", "µT"],
        });
        await WpfTestHost.PumpAsync();
        chart.StartAddTotalFieldCommand.Execute(null);
        chart.WizardSourceA = 0; chart.WizardSourceB = 1; chart.WizardSourceC = 3;
        chart.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Empty(chart.ComputedChannels);
        Assert.Contains("相同的磁场单位", chart.ComputationError);
        chart.WizardSourceC = 2;
        chart.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Equal("nT", Assert.Single(chart.ComputedChannels).Unit);
        chart.StartAddTotalFieldCommand.Execute(null);
        chart.WizardSourceA = 4; chart.WizardSourceB = 5; chart.WizardSourceC = 6;
        chart.ConfirmAddTotalFieldCommand.Execute(null);
        Assert.Equal("µT", chart.ComputedChannels[1].Unit);
        chart.StartAddGradientCommand.Execute(null);
        chart.WizardSourceA = 0; chart.WizardSourceB = 3;
        chart.ConfirmAddGradientCommand.Execute(null);
        Assert.Equal(2, chart.ComputedChannels.Count);
        chart.WizardSourceB = 1;
        chart.GradientBaselineDistance = 0;
        chart.ConfirmAddGradientCommand.Execute(null);
        Assert.Equal(2, chart.ComputedChannels.Count);
        Assert.Contains("有限正数", chart.ComputationError);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            chart.GradientBaselineDistance = 0.5;
            chart.ConfirmAddGradientCommand.Execute(null);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        Assert.Equal("nT/m", chart.ComputedChannels[2].Unit);
        Assert.Contains("0.5", chart.ComputedChannels[2].Formula);
        chart.AddComputedChannelCommand.Execute(null);
        var custom = chart.ComputedChannels[^1];
        Assert.Equal("CH0", custom.Formula);
        Assert.Equal("nT", custom.Unit);
        bool notified = false;
        custom.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(custom.Unit);
        custom.Formula = "CH3";
        custom.Unit = "°C";
        Assert.True(notified);
        var start = DateTime.Now;
        for (int i = 0; i < 3; i++)
            bus.PublishReading(new MagnetometerReading { Timestamp = start.AddMilliseconds(i * 10), ChannelValues = [100, 200, 300, 25, 1, 2, 3] });
        typeof(RealtimeChartViewModel).GetMethod("OnRenderTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(chart, new object?[] { null, EventArgs.Empty });
        var lines = chart.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().ToArray();
        Assert.Equal(11, lines.Length);
        Assert.Equal("°C", lines[^1].Axes.YAxis.Label.Text);
        Assert.Same(lines[3].Axes.YAxis, lines[^1].Axes.YAxis);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });
}
