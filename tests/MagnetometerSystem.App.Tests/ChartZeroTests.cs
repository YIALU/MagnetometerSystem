using System.ComponentModel;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
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

    [Fact]
    public Task ZeroUsesVisibleCurvesInTheTimeWindowAndLeavesRawStatisticsAlone() => WpfTestHost.RunAsync(async () =>
    {
        var bus = new DataBus();
        using var vm = new RealtimeChartViewModel(bus) { PlotControl = new ScottPlot.WPF.WpfPlot() };
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        Assert.False(vm.ZeroVisibleChannelsCommand.CanExecute(null));   // 还没有数据

        var start = new DateTime(2020, 1, 1);
        for (int i = 0; i <= 60; i++) Publish(bus, start, i);
        vm.TimeWindowSeconds = 10;
        vm.ChannelConfigs.Single(c => c.ChannelIndex == 2).Visible = false;
        vm.ComputedChannels.Add(new ComputedChannelDefinition { Name = "和", Formula = "CH0+CH1", Unit = "nT" });
        vm.RefreshPlot();
        var rawMeanBefore = vm.StatisticsRows.Single(r => r.Name == "CH0").Stats.Mean;
        var notified = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Assert.True(vm.ZeroVisibleChannelsCommand.CanExecute(null));
        Assert.False(vm.HasDisplayOffsets);
        vm.AutoScaleY = false;   // 手动范围按原始值设定，归零后改回自动，曲线不会移出视野
        vm.ZeroVisibleChannelsCommand.Execute(null);
        Assert.True(vm.AutoScaleY);

        // 时间窗口 10 s：只用 50..60 秒的 11 个点，CH0 均值 55。
        Assert.Equal(-55, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);
        Assert.Equal(-20000, vm.ChannelConfigs.Single(c => c.ChannelIndex == 1).DisplayOffset, 9);
        Assert.Equal(0, vm.ChannelConfigs.Single(c => c.ChannelIndex == 2).DisplayOffset);   // 隐藏的曲线不动
        Assert.Equal(-25, vm.ChannelConfigs.Single(c => c.ChannelIndex == 3).DisplayOffset, 9);
        Assert.Equal(-20055, vm.ComputedChannels[0].DisplayOffset, 9);
        Assert.True(vm.HasDisplayOffsets);
        Assert.Contains(nameof(RealtimeChartViewModel.HasDisplayOffsets), notified);

        // 偏移只作用于显示：滚动统计仍是原始值。
        vm.RefreshPlot();
        Assert.Equal(rawMeanBefore, vm.StatisticsRows.Single(r => r.Name == "CH0").Stats.Mean, 9);

        // 暂停时按冻结的画面归零，暂停后到达的数据不参与。
        vm.IsPaused = true;
        for (int i = 61; i <= 80; i++) Publish(bus, start, i);
        vm.ZeroVisibleChannelsCommand.Execute(null);
        Assert.Equal(-55, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);
        vm.IsPaused = false;
        vm.RefreshPlot();
        vm.ZeroVisibleChannelsCommand.Execute(null);
        Assert.Equal(-75, vm.ChannelConfigs.Single(c => c.ChannelIndex == 0).DisplayOffset, 9);

        notified.Clear();
        vm.ClearDisplayOffsetsCommand.Execute(null);
        Assert.All(vm.ChannelConfigs, c => Assert.Equal(0, c.DisplayOffset));
        Assert.Equal(0, vm.ComputedChannels[0].DisplayOffset);
        Assert.False(vm.HasDisplayOffsets);
        Assert.Contains(nameof(RealtimeChartViewModel.HasDisplayOffsets), notified);

        // 侧栏里手动填写偏移也会让“取消归零”出现；重新开始采集后的新通道同样被跟踪。
        vm.ChannelConfigs[1].DisplayOffset = 3;
        Assert.True(vm.HasDisplayOffsets);
        bus.PublishAcquisitionStopped();
        bus.PublishAcquisitionStarted(Configuration());
        await WpfTestHost.PumpAsync();
        vm.ChannelConfigs[0].DisplayOffset = 0;
        notified.Clear();
        vm.ChannelConfigs[0].DisplayOffset = 7;
        Assert.Contains(nameof(RealtimeChartViewModel.HasDisplayOffsets), notified);
        bus.PublishAcquisitionStopped();
        await WpfTestHost.PumpAsync();
    });
}
