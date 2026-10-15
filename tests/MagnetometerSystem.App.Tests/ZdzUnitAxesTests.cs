using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Charting;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using ScottPlot.WPF;

namespace MagnetometerSystem.App.Tests;

public class ZdzUnitAxesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ZdzChannelsUsePhysicalUnitAxesAndSurviveChannelReorder(bool autoScale) => WpfTestHost.RunAsync(async () =>
    {
        var protocol = ProtocolConfig.CreateZdzC08();
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { AutoScaleY = autoScale };
        using var renderer = new ChartRenderer(new WpfPlot());
        renderer.Attach(vm);
        var plot = renderer.SinglePlot.Plot;
        bus.PublishAcquisitionStarted(new SensorConfig
        {
            ChannelCountOverride = protocol.DerivedChannelCount,
            ChannelNamesOverride = protocol.DerivedChannelNames.ToArray(),
            ChannelUnitsOverride = protocol.DerivedChannelUnits.ToArray(),
        });
        await WpfTestHost.PumpAsync();
        vm.ChannelConfigs.Move(20, 0);
        var start = DateTime.Now;
        for (int i = 0; i < 4; i++)
            bus.PublishProcessedReading(new MagnetometerReading
            {
                Timestamp = start.AddMilliseconds(i * 10),
                ChannelValues = Enumerable.Range(0, 21).Select(ch => ch * 1000.0 + i).ToArray(),
            });
        vm.RefreshPlot();
        var lines = plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().ToArray();
        Assert.Equal(21, lines.Length);
        if (!autoScale)
        {
            Assert.Equal(vm.YMin, plot.Axes.Left.Range.Min);
            Assert.Equal(vm.YMax, plot.Axes.Left.Range.Max);
        }
        for (int ch = 0; ch < lines.Length; ch++)
        {
            Assert.Equal(protocol.DerivedChannelUnits[ch], lines[ch].Axes.YAxis.Label.Text);
            if (autoScale || !ReferenceEquals(lines[ch].Axes.YAxis, plot.Axes.Left))
            {
                var range = lines[ch].Axes.YAxis.Range;
                Assert.True(range.Min <= ch * 1000.0 && range.Max >= ch * 1000.0 + 3,
                    $"Channel {ch} values must be visible inside {range.Min}..{range.Max}");
            }
        }
        Assert.Same(lines[0].Axes.YAxis, lines[8].Axes.YAxis);
        Assert.NotSame(lines[0].Axes.YAxis, lines[9].Axes.YAxis);
        Assert.NotSame(lines[9].Axes.YAxis, lines[11].Axes.YAxis);
        Assert.NotSame(lines[11].Axes.YAxis, lines[14].Axes.YAxis);
        Assert.Equal("设备单位", lines[17].Axes.YAxis.Label.Text);
        Assert.Equal("m", lines[20].Axes.YAxis.Label.Text);
        foreach (var config in vm.ChannelConfigs) config.Visible = config.Unit == "nT";
        vm.RefreshPlot();
        Assert.Equal(9, plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().Count());
        Assert.Single(plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().Select(s => s.Axes.YAxis).Distinct());
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

}
