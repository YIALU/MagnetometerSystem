using System.Collections.Concurrent;
using System.IO;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.Tests;

/// <summary>
/// 实时拟合采集：样本与原始 CSV 的行一起写入；界面只经排队的通知更新；停止或换了一批之后，
/// 在途的回调与排队中的通知都不能再改样本、原始 CSV 或链路条状态。
/// </summary>
public class LiveCalibrationCollectorTests : IDisposable
{
    private readonly string _rawDir = Path.Combine(Path.GetTempPath(), $"collector_raw_{Guid.NewGuid():N}");
    private readonly DataBus _bus = new();
    private readonly CalibrationSampleSet _samples = new();
    private readonly CalibrationRawCsvRecorder _recorder;
    private readonly ConcurrentQueue<Action> _ui = new();   // 排队的界面通知，测试决定何时执行
    private readonly LiveCalibrationCollector _collector;
    private readonly List<(double[][] Added, int Count)> _added = new();
    private readonly List<string> _layoutChanges = new();
    private readonly List<string> _liveValues = new();

    public LiveCalibrationCollectorTests()
    {
        _recorder = new CalibrationRawCsvRecorder(_rawDir);
        _collector = new LiveCalibrationCollector(_bus, _samples, _recorder, _ui.Enqueue);
        _collector.SamplesAdded += (added, count) => _added.Add((added.ToArray(), count));
        _collector.LayoutChanged += _layoutChanges.Add;
        _collector.LiveValuesChanged += _liveValues.Add;
    }

    public void Dispose()
    {
        _collector.Dispose();
        _recorder.Close();
        if (Directory.Exists(_rawDir)) Directory.Delete(_rawDir, recursive: true);
    }

    private void RunUi()
    {
        while (_ui.TryDequeue(out var action)) action();
    }

    private async Task<LiveCalibrationPlan> StartAsync(int[] map, bool manual, int channels, string unit = "nT")
    {
        var names = Enumerable.Range(0, channels).Select(i => $"B{i}").ToArray();
        await _bus.PublishAcquisitionStartingAsync(new SensorConfig
        {
            Type = SensorType.Generic, ChannelCountOverride = channels,
            ChannelNamesOverride = names, ChannelUnitsOverride = Enumerable.Repeat(unit, channels).ToArray(),
        });
        var generation = _samples.Replace([], [], unit, map.Length, "live", map);
        var plan = new LiveCalibrationPlan(map, map.Select(i => names[i]).ToArray(),
            _bus.AcquisitionChannelNames, _bus.AcquisitionChannelUnits, manual, generation);
        _recorder.Close();
        _recorder.Open(new CalibrationRawCsvHeader("collector", "TriaxialFluxgate", unit, manual ? "Manual48" : "Continuous", plan.Labels));
        _collector.Start(plan);
        return plan;
    }

    private void Publish(params double[] values) =>
        _bus.PublishReading(new MagnetometerReading { Timestamp = new DateTime(2026, 1, 1, 8, 0, 0), ChannelValues = values });

    private string[] RawDataRows()
    {
        _recorder.Close();
        return File.ReadAllLines(Assert.IsType<string>(_recorder.FilePath))
            .SkipWhile(l => !l.StartsWith("point_index,")).Skip(1).ToArray();
    }

    [Fact]
    public async Task ContinuousReadingsAddSamplesAndRawRowsTogetherAndReachTheUiOnlyThroughThePost()
    {
        await StartAsync([3, 1, 2], manual: false, channels: 4);
        for (int i = 0; i < 3; i++) Publish(i, 10 + i, 20 + i, 30 + i);

        // 接收线程已加入样本并写了对应的行，界面还没有收到通知。
        Assert.Equal(3, _samples.Count);
        Assert.Equal(3, _recorder.Status.Rows);
        Assert.Empty(_added);

        RunUi();
        var (added, count) = Assert.Single(_added);   // 一批读数只排一次界面更新
        Assert.Equal(3, count);
        Assert.Equal([[30d, 10, 20], [31d, 11, 21], [32d, 12, 22]], added);
        Assert.Equal($"B3 {30d:0.0}   B1 {10d:0.0}   B2 {20d:0.0}", _liveValues[0]);   // 约 5 次/秒，只显示第一条

        _collector.Stop();
        Assert.Equal(["1,2026-01-01 08:00:00.000,30,10,20", "2,2026-01-01 08:00:00.000,31,11,21", "3,2026-01-01 08:00:00.000,32,12,22"],
            RawDataRows());
    }

    [Fact]
    public async Task StopDeliversPendingSamplesOnceAndLateCallbacksAddNothing()
    {
        await StartAsync([0, 1, 2], manual: false, channels: 3);
        Publish(1, 2, 3);

        _collector.Stop();   // 停止时交出还没显示的样本，不必等排队的通知
        Assert.Equal(1, Assert.Single(_added).Count);
        RunUi();   // 排队的刷新与实时值到达时已停止：不再通知
        Assert.Single(_added);
        Assert.Empty(_liveValues);

        // 退订前已经开始、停止之后才执行完的回调：不加入样本，不写行，也不排通知。
        _collector.OnReadingReceived(new MagnetometerReading { Timestamp = DateTime.Now, ChannelValues = [4, 5, 6] });
        Assert.Equal(1, _samples.Count);
        Assert.Equal(1, _recorder.Status.Rows);
        Assert.True(_ui.IsEmpty);
        Assert.False(_collector.IsCollecting);
    }

    [Fact]
    public async Task ManualStateIsPublishedOnlyWhileManualCollectionRuns()
    {
        await StartAsync([0, 1, 2], manual: true, channels: 3);
        var state = _bus.ManualOrthoState;
        Assert.True(state.IsActive);
        Assert.Equal(_recorder.FilePath, state.RawFilePath);
        Assert.Equal("等待数据缓冲...", state.StatusMessage);

        Publish(1, 2, 3);
        Assert.Equal("缓冲中 (1/10)", state.StatusMessage);
        Assert.Equal(0, _samples.Count);   // 手动模式只缓冲，记录点时才加入样本

        _collector.Stop();
        Assert.False(state.IsActive);
        RunUi();
        Assert.Empty(_liveValues);   // 停止前排队的实时值被丢弃

        // 停止之后，在途的回调、记录 / 撤销 / 清空和状态发布都不能把链路条改回“采集中”。
        _collector.OnReadingReceived(new MagnetometerReading { Timestamp = DateTime.Now, ChannelValues = [1, 2, 3] });
        _collector.PublishManualState(1, "已记录 1 点", true);
        Assert.Null(_collector.RecordPoint());
        Assert.Null(_collector.UndoLastPoint());
        Assert.Null(_collector.ClearPoints());
        Assert.False(state.IsActive);
    }

    [Fact]
    public async Task ManualPointsAverageTheLastTenReadingsAndKeepRawRowsInStep()
    {
        await StartAsync([0, 1, 2, 3, 4, 5], manual: true, channels: 6);
        Assert.Contains("还没有收到读数", _collector.RecordPoint()!.Error);
        for (int i = 0; i < 5; i++) Publish(1, 2, 3, 4, 5, 6);
        var early = _collector.RecordPoint()!;
        Assert.Contains("5/10", early.Error);
        Assert.Equal(0, _samples.Count);
        Assert.False(_bus.ManualOrthoState.HasEnoughBuffer);

        // 共 12 条读数，只取最近 10 条（i = 2..11）的均值；第二组与第一组逐点对应。
        for (int i = 0; i < 12; i++) Publish(i, i, i, 10 + i, 10 + i, 10 + i);
        Assert.True(_bus.ManualOrthoState.HasEnoughBuffer);
        Assert.Equal("缓冲就绪，可以记录", _bus.ManualOrthoState.StatusMessage);
        var point = _collector.RecordPoint()!;
        Assert.Null(point.Error);
        Assert.Equal([6.5, 6.5, 6.5], point.Sample);
        Assert.Equal((1, 10, 10), (point.Count, point.Buffered, point.Averaged));
        Assert.Equal([[16.5, 16.5, 16.5]], _samples.Snapshot().Second);

        Assert.Equal(2, _collector.RecordPoint()!.Count);
        Assert.Equal(1, _collector.UndoLastPoint());
        Assert.Equal(1, _collector.ClearPoints());
        Assert.Null(_collector.UndoLastPoint());   // 没有点可撤销
        Assert.Equal(1, _collector.RecordPoint()!.Count);
        Assert.Empty(_added);   // 手动点由调用方直接显示，不经过“新增样本”通知

        _collector.Stop();
        var rows = RawDataRows();
        Assert.Equal(5, rows.Length);
        Assert.StartsWith("1,", rows[0]);
        Assert.EndsWith(",6.5,6.5,6.5,16.5,16.5,16.5", rows[0]);
        Assert.StartsWith("2,", rows[1]);
        Assert.EndsWith("已撤销第 2 点", rows[2]);
        Assert.EndsWith("已清空之前的 1 点，重新记录", rows[3]);
        Assert.StartsWith("3,", rows[4]);
    }

    [Fact]
    public async Task LayoutChangesAreReportedOnceAndOnlyForTheRunningCollection()
    {
        var first = await StartAsync([0, 1, 2], manual: false, channels: 3);
        Publish(1, 2, 3, 4);   // 读数通道数与协议不符
        Publish(1, 2, 3, 4);
        Assert.Equal(0, _samples.Count);
        _collector.LayoutChanged += _ => _collector.Stop();   // 与校正页一致：收到通知即停止

        RunUi();
        Assert.Equal("读数的通道数与协议不一致，已停止拟合数据采集。", Assert.Single(_layoutChanges));
        Assert.Equal(0, _recorder.Status.Rows);

        // 上一次采集排队的通知在新一次采集开始之后才到达：属于已经停止的那次采集，丢弃。
        _layoutChanges.Clear();
        var second = await StartAsync([0, 1, 2], manual: false, channels: 3);
        Assert.NotSame(first, second);
        Publish(1, 2, 3);      // 排一次实时值
        Publish(1, 2, 3, 4);   // 排一次通道数不符的通知
        _collector.Stop();
        await StartAsync([0, 1, 2], manual: false, channels: 3);
        RunUi();
        Assert.Empty(_layoutChanges);
        Assert.Empty(_liveValues);
        Assert.True(_collector.IsCollecting);

        // 换了协议：通道名称或单位与开始时不同。
        await _bus.PublishAcquisitionStartingAsync(new SensorConfig
        {
            Type = SensorType.Generic, ChannelCountOverride = 3,
            ChannelNamesOverride = ["Bz", "By", "Bx"], ChannelUnitsOverride = ["nT", "nT", "nT"],
        });
        Publish(1, 2, 3);
        RunUi();
        Assert.Equal("连接的通道布局已改变，已停止拟合数据采集，请重新选择拟合通道后开始。", Assert.Single(_layoutChanges));
        Assert.Equal(0, _samples.Count);
    }

    [Fact]
    public async Task ReadingsForAReplacedBatchAreNotAddedAndWriteNoRow()
    {
        await StartAsync([0, 1, 2], manual: false, channels: 3);
        Publish(1, 2, 3);
        // 样本换成了另一批（例如导入）：带旧代号的读数不能混入，也不写原始行，行号仍与样本对应。
        _samples.Replace([[7, 8, 9]], [], "nT", 3);
        Publish(4, 5, 6);

        Assert.Equal([[7d, 8, 9]], _samples.Snapshot().First);
        Assert.Equal(1, _recorder.Status.Rows);
        RunUi();
        Assert.Single(Assert.Single(_added).Added);
    }

    [Fact]
    public async Task TheReceiveThreadNeverWaitsForTheUi()
    {
        await StartAsync([0, 1, 2], manual: false, channels: 3);
        const int count = 1000;
        // 界面线程一直不处理排队的通知：接收线程照样把全部读数收完。
        await Task.Run(() => { for (int i = 0; i < count; i++) Publish(i, i + 1, i + 2); }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(count, _samples.Count);
        Assert.Equal(count, _recorder.Status.Rows);
        RunUi();
        Assert.Equal(count, _added.Sum(a => a.Added.Length));
        Assert.Equal(count, _added[^1].Count);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (double)i), _added.SelectMany(a => a.Added).Select(s => s[0]));
    }

    [Fact]
    public async Task StartingTwiceIsRejectedAndDisposeDropsPendingSamples()
    {
        var plan = await StartAsync([0, 1, 2], manual: true, channels: 3);
        Assert.Throws<InvalidOperationException>(() => _collector.Start(plan));
        _collector.Stop();

        var generation = _samples.Replace([], [], "nT", 3, "live", [0, 1, 2]);
        _collector.Start(plan with { Manual = false, Generation = generation });
        Publish(1, 2, 3);
        _collector.Dispose();
        RunUi();
        Assert.Empty(_added);
        Assert.False(_bus.ManualOrthoState.IsActive);
        Publish(4, 5, 6);   // 已退订
        Assert.Equal(1, _samples.Count);
    }
}
