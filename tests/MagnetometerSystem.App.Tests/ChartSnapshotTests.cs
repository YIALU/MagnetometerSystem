using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using ScottPlot.Plottables;

namespace MagnetometerSystem.App.Tests;

public class ChartSnapshotTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2, 6, 8, 7)]
    [InlineData(6, 2, 4, 9)]
    [InlineData(2, 0, 8, 9)]
    [InlineData(0, 2, 0, 9)]
    [InlineData(0, 0, 0, 5)]
    public Task DisplayAndRawStatisticsKeepTheirOwnWindowsAndHiddenFormulaSources(
        double displayWindow, double statisticsWindow, int firstDisplayPoint, double rawMean) =>
        WpfTestHost.RunAsync(async () =>
        {
            var bus = new DataBus();
            using var vm = new RealtimeChartViewModel(bus)
            {
                PlotControl = new ScottPlot.WPF.WpfPlot(),
                TimeWindowSeconds = displayWindow,
                DownsampleTargetCount = 0,
            };
            await StartChartAsync(bus, vm, 2);
            ConfigureStatistics(vm, statisticsWindow);
            vm.ChannelConfigs[0].DisplayOffset = 7;
            vm.ChannelConfigs[1].Visible = false;
            vm.ComputedChannels.Add(new ComputedChannelDefinition
            {
                Name = "Hidden-source sum", Unit = "nT", Formula = "CH0 + CH1",
            });
            for (int i = 0; i <= 10; i++)
                bus.PublishProcessedReading(new MagnetometerReading
                {
                    Timestamp = Start.AddSeconds(i),
                    ChannelValues = [1000 + i, 2000 + 2 * i],
                    OriginalChannelValues = [i, 2 * i],
                    IsOrthogonalityCorrected = true,
                });

            vm.RefreshPlot();

            Assert.Equal(11, vm.DataPointCount);
            var lines = vm.PlotControl.Plot.GetPlottables().OfType<Scatter>().ToArray();
            Assert.Equal(2, lines.Length);
            var expectedTimes = Enumerable.Range(firstDisplayPoint, 11 - firstDisplayPoint).Select(i => (double)i).ToArray();
            AssertLine(Assert.Single(lines.Where(p => p.LegendText.StartsWith("CH0"))),
                expectedTimes, expectedTimes.Select(t => 1007 + t));
            AssertLine(Assert.Single(lines.Where(p => p.LegendText == "Hidden-source sum")),
                expectedTimes, expectedTimes.Select(t => 3000 + 3 * t));
            Assert.Contains($"CH0 Avg:{rawMean:F2}", vm.StatisticsText);
            Assert.Contains($"CH1 Avg:{2 * rawMean:F2}", vm.StatisticsText);
            Assert.Equal("10 nT", vm.ChannelConfigs[0].LatestValue);
            Assert.Equal("20 nT", vm.ChannelConfigs[1].LatestValue);
        });

    [Fact]
    public Task WrappedHistoryRemainsFrozenWhenPausedWindowExpandsAndNewReadingsArrive() =>
        WpfTestHost.RunAsync(async () =>
        {
            var bus = new DataBus();
            using var vm = new RealtimeChartViewModel(bus)
            {
                PlotControl = new ScottPlot.WPF.WpfPlot(),
                TimeWindowSeconds = 2,
                DownsampleTargetCount = 0,
            };
            await StartChartAsync(bus, vm, 1);
            ConfigureStatistics(vm, 6);
            var reading = new MagnetometerReading
            {
                ChannelValues = new double[1], OriginalChannelValues = new double[1],
                IsOrthogonalityCorrected = true,
            };
            void Feed(int from, int through)
            {
                for (int i = from; i <= through; i++)
                {
                    reading.Timestamp = Start.AddSeconds(i);
                    reading.ChannelValues[0] = 1000 + i;
                    reading.OriginalChannelValues![0] = i;
                    bus.PublishProcessedReading(reading);
                }
            }

            Feed(0, 100004);
            vm.RefreshPlot();
            Assert.Equal(100000, vm.DataPointCount);
            AssertLine(SingleLine(vm), [100002, 100003, 100004], [101002, 101003, 101004]);
            Assert.Contains($"CH0 Avg:{100001d:F2}", vm.StatisticsText);

            vm.IsPaused = true;
            Feed(100005, 100014);
            vm.TimeWindowSeconds = 0;
            vm.StatisticsConfig.WindowSeconds = 0;
            vm.PlotControl.Plot.Clear();
            vm.RefreshPlot();

            // Pausing freezes the complete retained history once, not just the old 2 s window.
            var frozen = SingleLine(vm).Data.GetScatterPoints().ToArray();
            Assert.Equal(100000, frozen.Length);
            Assert.Equal(5, frozen[0].X);
            Assert.Equal(1005, frozen[0].Y);
            Assert.Equal(100004, frozen[^1].X);
            Assert.Equal(101004, frozen[^1].Y);
            Assert.Equal(100000, vm.DataPointCount);
            Assert.Equal("100004 nT", vm.ChannelConfigs[0].LatestValue);
            Assert.Contains($"CH0 Avg:{50004.5:F2}", vm.StatisticsText);

            vm.TimeWindowSeconds = 2;
            vm.StatisticsConfig.WindowSeconds = 6;
            vm.IsPaused = false;
            vm.RefreshPlot();
            AssertLine(SingleLine(vm), [100012, 100013, 100014], [101012, 101013, 101014]);
            Assert.Equal(100000, vm.DataPointCount);
            Assert.Equal("100014 nT", vm.ChannelConfigs[0].LatestValue);
            Assert.Contains($"CH0 Avg:{100011d:F2}", vm.StatisticsText);
        });

    [Fact]
    public Task SmallWindowRefreshAllocationDoesNotGrowWithFull65ChannelRetainedHistory() =>
        WpfTestHost.RunAsync(async () =>
        {
            const int channels = 65;
            const int refreshes = 8;
            var bus = new DataBus();
            using var vm = new RealtimeChartViewModel(bus)
            {
                TimeWindowSeconds = 2,
                DownsampleTargetCount = 0,
            };
            await StartChartAsync(bus, vm, channels);
            ConfigureStatistics(vm, 5);
            var reading = new MagnetometerReading
            {
                ChannelValues = Enumerable.Range(0, channels).Select(i => 1000d + i).ToArray(),
                OriginalChannelValues = Enumerable.Range(0, channels).Select(i => (double)i).ToArray(),
                IsOrthogonalityCorrected = true,
            };
            void Feed(int from, int through)
            {
                for (int i = from; i <= through; i++)
                {
                    reading.Timestamp = Start.AddSeconds(i);
                    bus.PublishProcessedReading(reading);
                }
            }

            Feed(0, 999);
            long shortHistoryBytes = MeasureRefreshAllocations(vm, refreshes);
            Assert.Equal(1000, vm.DataPointCount);
            Feed(1000, 100004);
            long fullHistoryBytes = MeasureRefreshAllocations(vm, refreshes);

            Assert.Equal(100000, vm.DataPointCount);
            Assert.Equal(channels, vm.ChannelConfigs.Count);
            Assert.Equal("64 nT", vm.ChannelConfigs[64].LatestValue);
            Assert.Contains($"CH64 Avg:{64d:F2}", vm.StatisticsText);
            // No PlotControl is needed: actual RefreshPlot still captures all 65 channels
            // and computes raw statistics. Input retains every point; display downsampling is off.
            // Allow small runtime bookkeeping differences, but never a full-history copy
            // (the old implementation allocated over 100 MB per refresh in this case).
            Assert.True(fullHistoryBytes <= shortHistoryBytes + 256 * 1024,
                $"8 refreshes: short history {shortHistoryBytes:N0} B; full history {fullHistoryBytes:N0} B");
            Assert.True(fullHistoryBytes < refreshes * 2L * 1024 * 1024,
                $"Fixed small-window refreshes allocated {fullHistoryBytes:N0} B");
        });

    private static async Task StartChartAsync(DataBus bus, RealtimeChartViewModel vm, int channels)
    {
        bus.PublishAcquisitionStarted(new SensorConfig
        {
            Type = SensorType.Generic,
            ChannelCountOverride = channels,
            ChannelNamesOverride = Enumerable.Range(0, channels).Select(i => $"CH{i}").ToArray(),
            ChannelUnitsOverride = Enumerable.Repeat("nT", channels).ToArray(),
        });
        await WpfTestHost.PumpAsync();
        // Keep every measurement and drive the real refresh explicitly, without timer races.
        vm.StopRenderTimer();
    }

    private static void ConfigureStatistics(RealtimeChartViewModel vm, double seconds)
    {
        vm.StatisticsConfig.WindowSeconds = seconds;
        vm.StatisticsConfig.ShowStdDev = false;
        vm.StatisticsConfig.ShowPeakToPeak = false;
    }

    private static Scatter SingleLine(RealtimeChartViewModel vm) =>
        Assert.Single(vm.PlotControl!.Plot.GetPlottables().OfType<Scatter>());

    private static void AssertLine(Scatter line, IEnumerable<double> times, IEnumerable<double> values)
    {
        var points = line.Data.GetScatterPoints().ToArray();
        Assert.Equal(times, points.Select(p => p.X));
        Assert.Equal(values, points.Select(p => p.Y));
    }

    private static long MeasureRefreshAllocations(RealtimeChartViewModel vm, int count)
    {
        for (int i = 0; i < 4; i++) vm.RefreshPlot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) vm.RefreshPlot();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
