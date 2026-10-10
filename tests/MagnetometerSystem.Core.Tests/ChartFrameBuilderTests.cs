using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class ChartFrameBuilderTests
{
    private static readonly ChartAxes Axes = new(1, 3, AutoScroll: true, AutoScaleY: true, YMin: -10, YMax: 10, ShowGrid: true);
    private static readonly DisplayFilterSettings NoFilter = new(false, FilterType.MovingAverage, 5);

    private static ChartFrameBuilder Builder() => new(new DisplaySeriesPipeline(), new ComputedChannelEvaluator());

    private static ChartDisplaySettings Display(bool multiPlot, int downsample = 0) => new(multiPlot, NoFilter, downsample);

    // 拖动排序后的通道列表：温度排在最前，通道索引不变。
    private static ChannelDisplayConfig[] ReorderedChannels() =>
    [
        new() { Name = "温度", ChannelIndex = 2, Unit = "°C", ColorHex = "#FF1BAF7A" },
        new() { Name = "X", ChannelIndex = 0, Unit = "nT", ColorHex = "#FF2A78D6" },
        new() { Name = "Y", ChannelIndex = 1, Unit = "µT", ColorHex = "#FFEB6834" },
    ];

    // 4 个时间点，窗口取后 3 个。
    private static ChartWindowData Window() => new(
        Times: [0, 1, 2, 3],
        Channels: [[1, 2, 3, 4], [10, 20, 30, 40], [25, 26, 27, 28]],
        Start: 1, Count: 3, ChannelCount: 3);

    [Fact]
    public void SinglePlotDrawsChannelsByIndexThenComputedWithTheTemperatureAxisLast()
    {
        var sum = new ComputedChannelDefinition { Name = "和", Formula = "CH0 + CH1", Unit = "nT", ColorHex = "#FF112233", LineWidth = 2 };

        var frame = Builder().Build(Window(), Axes, Display(multiPlot: false), ReorderedChannels(), [sum]);

        Assert.False(frame.IsMultiPlot);
        Assert.Same(Axes, frame.Axes);
        Assert.Empty(frame.Panels);
        Assert.Equal(new[] { "nT", "µT", "°C" }, frame.Units);
        Assert.Equal(new[] { "X (nT)", "Y (µT)", "温度 (°C)", "和" }, frame.Curves.Select(c => c.Legend));
        Assert.Equal(new[] { "nT", "µT", "°C", "nT" }, frame.Curves.Select(c => c.Unit));
        Assert.All(frame.Curves, c => Assert.Equal([1, 2, 3], c.Xs));
        Assert.Equal([2, 3, 4], frame.Curves[0].Ys);
        Assert.Equal([20, 30, 40], frame.Curves[1].Ys);
        Assert.Equal([26, 27, 28], frame.Curves[2].Ys);
        Assert.Equal([22, 33, 44], frame.Curves[3].Ys);
        Assert.Equal<(byte, byte, byte, byte)?>((0xFF, 0x2A, 0x78, 0xD6), frame.Curves[0].Color);
        Assert.Equal<(byte, byte, byte, byte)?>((0xFF, 0x11, 0x22, 0x33), frame.Curves[3].Color);
        Assert.Equal(new[] { 1.5f, 1.5f, 1.5f, 2f }, frame.Curves.Select(c => c.LineWidth));
    }

    [Fact]
    public void SinglePlotLeavesOutHiddenChannelsAndChannelsWithoutWindowDataButDrawsUnconfiguredOnes()
    {
        var channels = ReorderedChannels();
        channels[2].Visible = false;   // Y
        // 温度只有一个点（晚到的通道）；第 4 个通道还没有显示配置。
        var window = Window() with { Channels = [[1, 2, 3, 4], [10, 20, 30, 40], [25], [5, 6, 7, 8]], ChannelCount = 4 };
        var disabled = new ComputedChannelDefinition { Name = "关", Formula = "CH0", Unit = "pT", Enabled = false };

        var frame = Builder().Build(window, Axes, Display(multiPlot: false), channels, [disabled]);

        Assert.Equal(2, frame.Curves.Count);
        Assert.Equal("X (nT)", frame.Curves[0].Legend);
        var unconfigured = frame.Curves[1];
        Assert.StartsWith("CH3", unconfigured.Legend);
        Assert.Equal([6, 7, 8], unconfigured.Ys);
        Assert.Null(unconfigured.Color);   // 绘图库默认配色
        Assert.Null(unconfigured.Unit);    // 默认的左轴
        Assert.DoesNotContain("µT", frame.Units);
        Assert.DoesNotContain("pT", frame.Units);
    }

    [Fact]
    public void MultiPlotFollowsTheListOrderReadsDataByChannelIndexAndKeepsTheSourceData()
    {
        var channels = ReorderedChannels();
        channels[1].DisplayOffset = 100;   // X
        channels[2].Visible = false;       // Y
        var window = Window();
        var sum = new ComputedChannelDefinition { Name = "和", Formula = "CH0 + CH1", Unit = "nT" };

        var frame = Builder().Build(window, Axes, Display(multiPlot: true), channels, [sum]);

        Assert.True(frame.IsMultiPlot);
        Assert.Same(Axes, frame.Axes);
        Assert.Empty(frame.Curves);
        Assert.Empty(frame.Units);
        Assert.Equal(new[] { "温度 (°C)", "X (nT)", "和 (nT)" }, frame.Panels.Select(p => p.AxisLabel));
        Assert.Equal(new[] { channels[0], channels[1], null }, frame.Panels.Select(p => p.Channel));
        Assert.Equal([26, 27, 28], frame.Panels[0].Curve!.Ys);
        Assert.Equal([102, 103, 104], frame.Panels[1].Curve!.Ys);
        // 显示偏移只加在曲线副本上：缓冲不变，计算通道也不含 X 的偏移。
        Assert.Equal([1, 2, 3, 4], window.Channels[0]);
        Assert.Equal([22, 33, 44], frame.Panels[2].Curve!.Ys);
        Assert.Equal("显示窗口\n" + StatisticsResultItem.Compute("X", [102, 103, 104]).FormatMultiline(), frame.Panels[1].Statistics);
        Assert.Equal("显示窗口\n" + StatisticsResultItem.Compute("和", [22, 33, 44]).FormatMultiline(), frame.Panels[2].Statistics);
        Assert.All(frame.Panels, p => Assert.Null(p.Curve!.Legend));
    }

    [Fact]
    public void MultiPlotKeepsAnEmptyPanelForAChannelWithoutWindowDataSoLaterPanelsStayInPlace()
    {
        var window = Window() with { Channels = [[1, 2, 3, 4], [10, 20, 30, 40], [25]] };   // 温度晚到

        var frame = Builder().Build(window, Axes, Display(multiPlot: true), ReorderedChannels(), []);

        Assert.Equal(new[] { "温度 (°C)", "X (nT)", "Y (µT)" }, frame.Panels.Select(p => p.AxisLabel));
        Assert.Null(frame.Panels[0].Curve);
        Assert.Null(frame.Panels[0].Statistics);
        Assert.Equal([2, 3, 4], frame.Panels[1].Curve!.Ys);
        Assert.Equal([20, 30, 40], frame.Panels[2].Curve!.Ys);
    }

    [Fact]
    public void ComputedChannelsFillMissingPointsWithZeroAndSkipDisabledOrBlankFormulas()
    {
        // CH1 只有前两个点。
        var window = Window() with { Channels = [[1, 2, 3, 4], [10, 20]], ChannelCount = 2 };
        ComputedChannelDefinition[] computed =
        [
            new() { Name = "空", Formula = " ", Unit = "nT" },
            new() { Name = "关", Formula = "CH0", Unit = "nT", Enabled = false },
            new() { Name = "和", Formula = "CH0 + CH1", Unit = "nT", DisplayOffset = 1000 },
        ];

        var frame = Builder().Build(window, Axes, Display(multiPlot: false), [], computed);

        // CH1 在窗口内缺数据，不画；计算通道按 0 补缺的点，再加显示偏移。
        Assert.Equal(2, frame.Curves.Count);
        Assert.StartsWith("CH0", frame.Curves[0].Legend);
        Assert.Equal("和", frame.Curves[1].Legend);
        Assert.Equal([1022, 1003, 1004], frame.Curves[1].Ys);
    }

    [Fact]
    public void DownsamplingThinsTheCurveButWindowStatisticsUseEveryDisplayedPoint()
    {
        var times = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        var values = times.Select(t => t % 7).ToArray();
        var window = new ChartWindowData(times, [values], Start: 0, Count: 100, ChannelCount: 1);
        ChannelDisplayConfig[] channels = [new() { Name = "X", ChannelIndex = 0 }];

        var single = Builder().Build(window, Axes, Display(multiPlot: false, downsample: 10), channels, []);
        var multi = Builder().Build(window, Axes, Display(multiPlot: true, downsample: 10), channels, []);

        Assert.Equal(10, single.Curves[0].Xs.Length);
        Assert.Equal(10, multi.Panels[0].Curve!.Ys.Length);
        Assert.Equal("显示窗口\n" + StatisticsResultItem.Compute("X", values).FormatMultiline(), multi.Panels[0].Statistics);
    }
}
