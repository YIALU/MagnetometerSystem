using System.Collections.ObjectModel;
using System.ComponentModel;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.Tests;

/// <summary>采集页“一键归零”：只改显示偏移，按当前时间窗口和可见曲线计算，不影响原始统计。</summary>
public class ChartZeroTests
{
    private static SensorConfig Configuration() => new()
    {
        Type = SensorType.Generic, SampleRate = 1, ChannelCountOverride = 4,
        ChannelNamesOverride = ["CH0", "CH1", "CH2", "温度"],
        ChannelUnitsOverride = ["nT", "nT", "nT", "°C"],
    };

    private static void Publish(DataBus bus, DateTime start, int second) => bus.PublishProcessedReading(new MagnetometerReading
    {
        Timestamp = start.AddSeconds(second), SensorType = SensorType.Generic,
        ChannelValues = [second, 20000, 30000, 25],
    });

    [Theory]
    [InlineData(FilterType.MovingAverage)]
    [InlineData(FilterType.Median)]
    public Task ZeroCentresTheFilteredCurveThatIsActuallyPlotted(FilterType filter) => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { PlotControl = new ScottPlot.WPF.WpfPlot() };
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        var start = new DateTime(2020, 1, 1);
        // 窗口末端一个尖峰：滤波后的均值与原始均值明显不同。
        for (int i = 0; i <= 60; i++)
            bus.PublishProcessedReading(new MagnetometerReading
            {
                Timestamp = start.AddSeconds(i), SensorType = SensorType.Generic,
                ChannelValues = [i == 60 ? 1000 : i, 20000, 30000, 25],
            });
        vm.TimeWindowSeconds = 10;
        vm.IsFilterEnabled = true;
        vm.SelectedFilterType = filter;
        vm.FilterWindowSize = 5;
        vm.RefreshPlot();

        vm.Offsets.ZeroVisibleChannelsCommand.Execute(null);

        double[] window = [.. Enumerable.Range(50, 10).Select(i => (double)i), 1000];
        var processor = new DataProcessor();
        var filtered = filter == FilterType.MovingAverage ? processor.MovingAverage(window, 5) : processor.MedianFilter(window, 5);
        var offset = vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset;
        Assert.Equal(-filtered.Average(), offset, 9);
        Assert.NotEqual(-window.Average(), offset, 3);
        // 实际画出的 CH0 曲线（加偏移后再滤波）均值为 0。
        var curve = vm.PlotControl.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>()
            .Single(p => p.LegendText.StartsWith("CH0"));
        Assert.Equal(0, curve.Data.GetScatterPoints().Average(pt => pt.Y), 9);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task ZeroUsesVisibleCurvesInTheTimeWindowAndLeavesRawStatisticsAlone() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { PlotControl = new ScottPlot.WPF.WpfPlot() };
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        Assert.False(vm.Offsets.ZeroVisibleChannelsCommand.CanExecute(null));   // 还没有数据

        var start = new DateTime(2020, 1, 1);
        for (int i = 0; i <= 60; i++) Publish(bus, start, i);
        vm.TimeWindowSeconds = 10;
        vm.ChannelConfigs.Single(c => c.ChannelIndex == 2).Visible = false;
        vm.ComputedChannels.Add(new ComputedChannelDefinition { Name = "和", Formula = "CH0+CH1", Unit = "nT" });
        vm.RefreshPlot();
        var rawMeanBefore = vm.StatisticsRows.Single(r => r.Name == "CH0").Stats.Mean;
        var notified = new List<string?>();
        ((INotifyPropertyChanged)vm.Offsets).PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Assert.True(vm.Offsets.ZeroVisibleChannelsCommand.CanExecute(null));
        Assert.False(vm.Offsets.HasDisplayOffsets);
        vm.AutoScaleY = false;   // 手动范围按原始值设定，归零后改回自动，曲线不会移出视野
        vm.Offsets.ZeroVisibleChannelsCommand.Execute(null);
        Assert.True(vm.AutoScaleY);

        // 时间窗口 10 s：只用 50..60 秒的 11 个点，CH0 均值 55。
        Assert.Equal(-55, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);
        Assert.Equal(-20000, vm.ChannelConfigs.Single(c => c.ChannelIndex == 1).DisplayOffset, 9);
        Assert.Equal(0, vm.ChannelConfigs.Single(c => c.ChannelIndex == 2).DisplayOffset);   // 隐藏的曲线不动
        Assert.Equal(-25, vm.ChannelConfigs.Single(c => c.ChannelIndex == 3).DisplayOffset, 9);
        Assert.Equal(-20055, vm.ComputedChannels[0].DisplayOffset, 9);
        Assert.True(vm.Offsets.HasDisplayOffsets);
        Assert.Contains(nameof(DisplayOffsetViewModel.HasDisplayOffsets), notified);

        // 偏移只作用于显示：滚动统计仍是原始值。
        vm.RefreshPlot();
        Assert.Equal(rawMeanBefore, vm.StatisticsRows.Single(r => r.Name == "CH0").Stats.Mean, 9);

        // 暂停时按冻结的画面归零，暂停后到达的数据不参与。
        vm.IsPaused = true;
        for (int i = 61; i <= 80; i++) Publish(bus, start, i);
        vm.Offsets.ZeroVisibleChannelsCommand.Execute(null);
        Assert.Equal(-55, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);
        vm.IsPaused = false;
        vm.RefreshPlot();
        vm.Offsets.ZeroVisibleChannelsCommand.Execute(null);
        Assert.Equal(-75, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);

        notified.Clear();
        vm.Offsets.ClearDisplayOffsetsCommand.Execute(null);
        Assert.All(vm.ChannelConfigs, c => Assert.Equal(0, c.DisplayOffset));
        Assert.Equal(0, vm.ComputedChannels[0].DisplayOffset);
        Assert.False(vm.Offsets.HasDisplayOffsets);
        Assert.Contains(nameof(DisplayOffsetViewModel.HasDisplayOffsets), notified);

        // 侧栏里手动填写偏移也会让“取消归零”出现；重新开始采集后的新通道同样被跟踪。
        vm.ChannelConfigs[1].DisplayOffset = 3;
        Assert.True(vm.Offsets.HasDisplayOffsets);
        bus.PublishAcquisitionStopped();
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        vm.ChannelConfigs[0].DisplayOffset = 0;
        notified.Clear();
        vm.ChannelConfigs[0].DisplayOffset = 7;
        Assert.Contains(nameof(DisplayOffsetViewModel.HasDisplayOffsets), notified);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public Task ChannelOffsetButtonsUseTheWholeDisplayBufferAndClearOnlyTheirChannel() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus);
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        var start = new DateTime(2020, 1, 1);
        // 显示值（校正后）与原始值不同；“按均值归零”用整个缓冲的显示值，不看时间窗口。
        for (int i = 0; i <= 10; i++)
            bus.PublishProcessedReading(new MagnetometerReading
            {
                Timestamp = start.AddSeconds(i), SensorType = SensorType.Generic,
                ChannelValues = [1000 + i, 20000, 30000, 25],
                OriginalChannelValues = [i, 20000, 30000, 25], IsOrthogonalityCorrected = true,
            });
        vm.TimeWindowSeconds = 2;
        var sum = new ComputedChannelDefinition { Name = "和", Formula = "CH0+CH1", Unit = "nT" };
        vm.ComputedChannels.Add(sum);

        vm.Offsets.AutoOffsetChannelCommand.Execute(0);
        vm.Offsets.AutoOffsetChannelCommand.Execute(1);
        vm.Offsets.AutoOffsetComputedChannelCommand.Execute(sum);
        Assert.Equal(-1005, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);
        Assert.Equal(-20000, vm.ChannelConfigs.Single(c => c.ChannelIndex == 1).DisplayOffset, 9);
        Assert.Equal(-21005, sum.DisplayOffset, 9);

        vm.Offsets.ClearChannelOffsetCommand.Execute(0);
        Assert.Equal(0, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset);
        Assert.Equal(-20000, vm.ChannelConfigs.Single(c => c.ChannelIndex == 1).DisplayOffset, 9);
        Assert.True(vm.Offsets.HasDisplayOffsets);

        // 没有这个通道时什么都不改。
        vm.Offsets.AutoOffsetChannelCommand.Execute(9);
        Assert.Equal(0, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });

    [Fact]
    public void OffsetChangesAreTrackedForExistingChannelsAndStopAfterRemovalOrDispose()
    {
        var channels = new ObservableCollection<ChannelDisplayConfig>(ChannelDisplayConfig.CreateDefaults(2, ["Bx", "By"]));
        var computed = new ObservableCollection<ComputedChannelDefinition> { new() { Name = "和", Formula = "CH0+CH1" } };
        var offsets = new DisplayOffsetViewModel(channels, computed, new ChartSampleBuffer(), new ComputedChannelEvaluator(),
            new DisplaySeriesPipeline(), () => default, _ => { });
        var notified = new List<string?>();
        offsets.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // 创建前就在列表里的通道同样被跟踪。
        channels[1].DisplayOffset = 3;
        computed[0].DisplayOffset = -1;
        Assert.Equal(2, notified.Count(n => n == nameof(DisplayOffsetViewModel.HasDisplayOffsets)));
        Assert.True(offsets.HasDisplayOffsets);

        // 移出列表的通道不再触发通知，释放后也不再通知。
        var removed = channels[1];
        channels.RemoveAt(1);
        notified.Clear();
        removed.DisplayOffset = 5;
        Assert.Empty(notified);
        offsets.Dispose();
        channels[0].DisplayOffset = 1;
        computed.Clear();
        Assert.Empty(notified);
    }
}
