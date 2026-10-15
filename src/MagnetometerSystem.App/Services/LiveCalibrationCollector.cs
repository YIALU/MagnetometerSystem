using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.Services;

/// <summary>实时拟合采集开始时冻结的参数。</summary>
/// <param name="Map">所选通道索引：X、Y、Z[、X2、Y2、Z2]。</param>
/// <param name="Labels">所选通道的显示名称，与 <paramref name="Map"/> 一一对应。</param>
/// <param name="LayoutNames">开始时连接的协议通道名称。</param>
/// <param name="LayoutUnits">开始时连接的协议通道单位。</param>
/// <param name="Manual">手动模式：每点取最近 10 条读数的均值；否则每条读数都是一个样本。</param>
/// <param name="Generation">样本集里这一批的代号。</param>
public sealed record LiveCalibrationPlan(
    int[] Map, string[] Labels, IReadOnlyList<string> LayoutNames, IReadOnlyList<string> LayoutUnits, bool Manual, long Generation);

/// <summary>记录一个手动点的结果：<see cref="Error"/> 为 null 时 <see cref="Sample"/> 已加入样本集，原始 CSV 已写入对应的行。</summary>
/// <param name="Sample">第一组三轴的均值样本。</param>
/// <param name="Count">记录后的样本数。</param>
/// <param name="Buffered">记录时缓冲的读数条数。</param>
/// <param name="Averaged">参与求均值的读数条数。</param>
/// <param name="Error">不能记录的原因。</param>
public sealed record ManualPointResult(double[] Sample, int Count, int Buffered, int Averaged, string? Error);

/// <summary>
/// 实时拟合采集：订阅读数，按开始时冻结的通道取样本，加入样本集并在同一临界区写原始 CSV 的对应行；
/// 手动模式保留最近 10 条读数，记录点时取均值，并发布链路条上的手动采集状态。
/// 读数在接收线程到达：这里只入队，经注入的 postToUi 排队通知界面，从不同步等待界面线程（接收端在自己的锁内回调）。
/// 锁顺序固定为“采集器 → 样本集 / 原始 CSV”，后两者不回调采集器。采集状态与链路条状态在同一把锁内改变，
/// 停止或换了一批之后，在途的读数回调不能混入样本，也不能把链路条改回“采集中”；排队中的界面通知到达时也会丢弃。
/// </summary>
public sealed class LiveCalibrationCollector : IDisposable
{
    /// <summary>手动模式每个点取均值的读数条数。</summary>
    public const int RecentBufferSize = 10;

    private readonly DataBus _dataBus;
    private readonly CalibrationSampleSet _samples;
    private readonly CalibrationRawCsvRecorder _recorder;
    private readonly Action<Action> _postToUi;
    private readonly Action<Action> _postSamplesToUi;
    private readonly object _gate = new();
    private readonly Queue<MagnetometerReading> _recentReadings = new(RecentBufferSize);
    private readonly List<double[]> _pendingUiSamples = new();
    private LiveCalibrationPlan? _plan;   // 采集中为本次采集的参数，否则为 null
    private int _uiFlushPending;
    private long _lastLiveValuesTicks;

    /// <param name="dataBus">读数来源，以及链路条上的手动采集状态。</param>
    /// <param name="samples">样本集：采集只向开始时那一批追加。</param>
    /// <param name="recorder">原始 CSV：由调用方在开始前打开、停止后关闭。</param>
    /// <param name="postToUi">把通知排到界面线程执行（不等待）；已在界面线程时可以直接执行。</param>
    /// <param name="postSamplesToUi">把新增样本的刷新排到界面线程（不等待），可以用更低的优先级，让高速读数时的刷新排在输入与渲染之后；省略时同 <paramref name="postToUi"/>。</param>
    public LiveCalibrationCollector(DataBus dataBus, CalibrationSampleSet samples, CalibrationRawCsvRecorder recorder,
        Action<Action> postToUi, Action<Action>? postSamplesToUi = null)
    {
        _dataBus = dataBus;
        _samples = samples;
        _recorder = recorder;
        _postToUi = postToUi;
        _postSamplesToUi = postSamplesToUi ?? postToUi;
    }

    /// <summary>界面线程：连续模式新增的第一组样本已经可以显示；第二个参数是此时的样本数。</summary>
    public event Action<IReadOnlyList<double[]>, int>? SamplesAdded;

    /// <summary>界面线程：连接的通道布局或读数的通道数与开始时不同，这批数据不能继续采集；参数是给用户的说明。</summary>
    public event Action<string>? LayoutChanged;

    /// <summary>界面线程：最近一条读数中所选通道的值，约每 0.2 秒一次。</summary>
    public event Action<string>? LiveValuesChanged;

    /// <summary>正在采集。</summary>
    public bool IsCollecting
    {
        get { lock (_gate) return _plan != null; }
    }

    /// <summary>手动模式缓冲的读数条数。</summary>
    public int BufferedCount
    {
        get { lock (_gate) return _recentReadings.Count; }
    }

    /// <summary>开始采集并订阅读数。手动模式立即发布“采集中”的链路条状态。</summary>
    public void Start(LiveCalibrationPlan plan)
    {
        lock (_gate)
        {
            if (_plan != null) throw new InvalidOperationException("拟合数据采集已在进行。");
            _plan = plan;
            _recentReadings.Clear();
            _pendingUiSamples.Clear();
            if (plan.Manual) _dataBus.ManualOrthoState.Update(true, 0, _recorder.FilePath, "等待数据缓冲...", false);
        }
        _dataBus.ReadingReceived += OnReadingReceived;
    }

    /// <summary>界面线程：停止采集，结束链路条的手动状态，并把还没显示的样本交给界面。</summary>
    public void Stop()
    {
        _dataBus.ReadingReceived -= OnReadingReceived;
        lock (_gate)
        {
            _plan = null;
            _dataBus.ManualOrthoState.Update(false, 0, null, "", false);
        }
        FlushPendingSamples();
    }

    /// <summary>释放：停止采集并丢弃还没显示的样本，不再通知界面。</summary>
    public void Dispose()
    {
        _dataBus.ReadingReceived -= OnReadingReceived;
        lock (_gate)
        {
            _plan = null;
            _recentReadings.Clear();
            _pendingUiSamples.Clear();
            _dataBus.ManualOrthoState.Update(false, 0, null, "", false);
        }
    }

    /// <summary>接收线程上的读数回调。测试直接调用它，模拟退订前已经开始、停止采集后才执行完的回调。</summary>
    internal void OnReadingReceived(MagnetometerReading reading)
    {
        LiveCalibrationPlan? plan;
        lock (_gate) plan = _plan;
        if (plan == null) return;

        // 连接的通道布局与开始采集时不同，或读数通道数与协议不符：不混入这批数据，在界面线程停止采集。
        string? layoutProblem = null;
        if (!SameLayout(_dataBus.AcquisitionChannelUnits, plan.LayoutUnits)
            || !SameLayout(_dataBus.AcquisitionChannelNames, plan.LayoutNames))
            layoutProblem = "连接的通道布局已改变，已停止拟合数据采集，请重新选择拟合通道后开始。";
        else if (reading.ChannelValues.Length != plan.LayoutUnits.Count || plan.Map.Length == 0)
            layoutProblem = "读数的通道数与协议不一致，已停止拟合数据采集。";
        if (layoutProblem != null)
        {
            _postToUi(() => { if (IsCurrent(plan)) LayoutChanged?.Invoke(layoutProblem); });
            return;
        }

        bool added = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_plan, plan)) return;
            // 任何模式都先维护“最近 10 条”队列
            if (_recentReadings.Count >= RecentBufferSize) _recentReadings.Dequeue();
            _recentReadings.Enqueue(reading);
            int buffered = _recentReadings.Count;

            if (plan.Manual)
            {
                // 手动模式：只保持队列，记录点时才加入样本；更新缓冲就绪状态
                bool enough = buffered >= RecentBufferSize;
                _dataBus.ManualOrthoState.Update(true, _samples.Count, _recorder.FilePath,
                    enough ? "缓冲就绪，可以记录" : $"缓冲中 ({buffered}/{RecentBufferSize})", enough);
            }
            else
            {
                // 连续模式：每条读数取所选的三个（双三轴为六个）通道作为样本，样本与原始 CSV 的行在同一临界区写入
                var values = Pick(reading.ChannelValues, plan.Map);
                var first = new[] { values[0], values[1], values[2] };
                var second = plan.Map.Length == 6 ? new[] { values[3], values[4], values[5] } : null;
                if (_samples.TryAdd(plan.Generation, first, second) >= 0)
                {
                    _pendingUiSamples.Add(first);
                    _recorder.AppendPoint(reading.Timestamp, values);
                    added = true;
                }
            }
        }
        PublishLiveValues(plan, reading);

        if (added && Interlocked.Exchange(ref _uiFlushPending, 1) == 0) _postSamplesToUi(FlushPendingSamples);
    }

    /// <summary>
    /// 界面线程：把最近 10 条读数对每个所选通道求均值，作为一个点加入样本集并写原始 CSV。
    /// 未在手动采集或这一批已经换了时返回 null；缓冲不足等不能记录的情况在 <see cref="ManualPointResult.Error"/> 中说明。
    /// </summary>
    public ManualPointResult? RecordPoint()
    {
        lock (_gate)
        {
            if (_plan is not { Manual: true } plan) return null;
            int buffered = _recentReadings.Count;
            if (buffered == 0)
                return Rejected("还没有收到读数：确认设备已连接并在输出数据", buffered);
            // 每个点承诺为最近 RecentBufferSize 条读数的均值；不足时不记录（页面按钮、链路条按钮和快捷键一致）。
            if (buffered < RecentBufferSize)
                return Rejected($"缓冲中（{buffered}/{RecentBufferSize}），收满 {RecentBufferSize} 条读数后再记录", buffered);

            var map = plan.Map;
            int n = map.Length, validCount = 0;
            var sums = new double[n];
            DateTime lastTs = DateTime.Now;
            foreach (var r in _recentReadings)
            {
                if (r.ChannelValues.Length != plan.LayoutUnits.Count) continue;
                for (int i = 0; i < n; i++) sums[i] += r.ChannelValues[map[i]];
                validCount++;
                lastTs = r.Timestamp;
            }
            if (validCount < RecentBufferSize)
                return Rejected($"最近 {RecentBufferSize} 条读数中只有 {validCount} 条与协议通道数一致，不能记录", buffered);

            var avg = new double[n];
            for (int i = 0; i < n; i++) avg[i] = sums[i] / validCount;
            var first = new[] { avg[0], avg[1], avg[2] };
            int count = _samples.TryAdd(plan.Generation, first, n == 6 ? new[] { avg[3], avg[4], avg[5] } : null);
            if (count < 0) return null;
            _recorder.AppendPoint(lastTs, avg);
            return new ManualPointResult(first, count, buffered, validCount, null);
        }
    }

    /// <summary>界面线程：撤销最后一个手动点，原始 CSV 只追加一行注释。返回剩下的样本数；未在手动采集或没有样本时返回 null。</summary>
    public int? UndoLastPoint()
    {
        lock (_gate)
        {
            if (_plan is not { Manual: true } plan) return null;
            int count = _samples.TryRemoveLast(plan.Generation);
            if (count < 0) return null;
            _recorder.AppendComment($"已撤销第 {count + 1} 点");
            return count;
        }
    }

    /// <summary>界面线程：清空已记录的手动点，继续采集；原始 CSV 追加注释说明之前的点已作废。返回清掉的点数；未在手动采集时返回 null。</summary>
    public int? ClearPoints()
    {
        lock (_gate)
        {
            if (_plan is not { Manual: true } plan) return null;
            int removed = _samples.TryClear(plan.Generation);
            if (removed < 0) return null;
            _recorder.AppendComment($"已清空之前的 {removed} 点，重新记录");
            return removed;
        }
    }

    /// <summary>
    /// 只在手动采集进行中发布链路条的“采集中”状态。检查与发布在同一把锁内，停止采集之后不会把状态改回采集中。
    /// 订阅者只有界面绑定，跨线程通知由绑定异步转到界面线程，不会在锁内等待界面。
    /// </summary>
    public void PublishManualState(int points, string status, bool enoughBuffer)
    {
        lock (_gate)
        {
            if (_plan is not { Manual: true }) return;
            _dataBus.ManualOrthoState.Update(true, points, _recorder.FilePath, status, enoughBuffer);
        }
    }

    private bool IsCurrent(LiveCalibrationPlan plan)
    {
        lock (_gate) return ReferenceEquals(_plan, plan);
    }

    private static ManualPointResult Rejected(string reason, int buffered) => new([], 0, buffered, 0, reason);

    /// <summary>界面线程：把接收线程新增的样本交给界面。</summary>
    private void FlushPendingSamples()
    {
        double[][] added;
        int count;
        lock (_gate)
        {
            added = _pendingUiSamples.ToArray();
            _pendingUiSamples.Clear();
            count = _samples.Count;
            Interlocked.Exchange(ref _uiFlushPending, 0);
        }
        if (added.Length > 0) SamplesAdded?.Invoke(added, count);
    }

    /// <summary>界面显示的实时值，限制在约 5 次/秒，避免每条读数都排一次界面更新。</summary>
    private void PublishLiveValues(LiveCalibrationPlan plan, MagnetometerReading reading)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastLiveValuesTicks);
        if (now - last < TimeSpan.TicksPerMillisecond * 200 || Interlocked.CompareExchange(ref _lastLiveValuesTicks, now, last) != last) return;
        var map = plan.Map;
        var labels = plan.Labels;
        if (map.Length == 0 || reading.ChannelValues.Length <= map.Max()) return;
        var text = string.Join("   ", map.Select((channel, i) =>
            $"{(i < labels.Length ? labels[i] : "XYZ"[i % 3].ToString())} {reading.ChannelValues[channel]:0.0}"));
        _postToUi(() => { if (IsCurrent(plan)) LiveValuesChanged?.Invoke(text); });
    }

    private static double[] Pick(double[] values, int[] map)
    {
        var picked = new double[map.Length];
        for (int i = 0; i < map.Length; i++) picked[i] = values[map[i]];
        return picked;
    }

    /// <summary>通道布局是否未变：同一份冻结列表，或内容相同（同一协议重新连接）。</summary>
    private static bool SameLayout(IReadOnlyList<string> current, IReadOnlyList<string> frozen) =>
        ReferenceEquals(current, frozen) || current.SequenceEqual(frozen);
}
