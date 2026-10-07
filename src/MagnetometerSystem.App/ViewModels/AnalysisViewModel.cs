using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>分析页可选的协议通道（名称、单位来自会话记录）。</summary>
public partial class AnalysisChannelOption : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Label => string.IsNullOrEmpty(Unit) ? Name : $"{Name}（{Unit}）";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>一个通道的分析结果行。</summary>
public sealed class AnalysisResultRow
{
    public required AnalysisChannelOption Channel { get; init; }
    public required DriftNoiseResult Result { get; init; }
    public required double[] Seconds { get; init; }
    public required double[] Values { get; init; }
    public string Name => Channel.Name;
    public string Unit => Channel.Unit;
    public string WarningText => string.Join(" ", Result.Warnings);
}

/// <summary>
/// 独立数据分析页（REQ-005 首版）：从已保存会话按通道和时间段读取原始值，
/// 计算基础统计、短时噪声与长时漂移。只读数据库，不写入任何会话，也不进入采集数据流。
/// </summary>
public partial class AnalysisViewModel : ObservableObject
{
    /// <summary>按时间分块读取，块之间可取消；避免长会话一次性占满内存。</summary>
    private static readonly TimeSpan LoadChunk = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 一次分析在内存中保留的数值上限（时间戳 + 所选通道值）。分析需要所选时间段的全部原始点，
    /// 不做降采样；超过上限时停止读取并提示缩短时间段或减少通道，避免长时高频会话占满内存。
    /// </summary>
    internal long MaxAnalysisValues { get; set; } = 20_000_000;

    private readonly IDataStorageService _storage;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<AnalysisChannelOption> Channels { get; } = new();
    public ObservableCollection<AnalysisResultRow> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private SessionInfo? _selectedSession;

    [ObservableProperty] private string _rangeStartText = "";
    [ObservableProperty] private string _rangeEndText = "";
    [ObservableProperty] private double _noiseWindowSeconds = 10;
    [ObservableProperty] private bool _detrendNoiseWindows = true;
    [ObservableProperty] private double _driftSegmentSeconds = 60;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "选择已保存的会话、通道和时间段后开始分析。分析只读取数据库，不修改原始数据。";
    [ObservableProperty] private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private AnalysisResultRow? _focusedResult;

    /// <summary>上一次计算使用的设置，用于导出时如实记录。</summary>
    [ObservableProperty] private string _analysisSettingsText = "";

    public AnalysisViewModel(IDataStorageService storage) => _storage = storage;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshSessionsAsync();
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        try
        {
            var keep = SelectedSession?.Id;
            var sessions = await _storage.GetSessionsAsync();
            Sessions.Clear();
            foreach (var s in sessions.Where(s => s.EndedAt != null && s.LegacyDataTable == null).OrderByDescending(s => s.StartedAt))
                Sessions.Add(s);
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == keep) ?? Sessions.FirstOrDefault();
        }
        catch (Exception ex) { Report($"读取会话列表失败：{ex.Message}", true); }
    }

    partial void OnSelectedSessionChanged(SessionInfo? value)
    {
        Channels.Clear();
        Results.Clear();
        FocusedResult = null;
        RangeStartText = RangeEndText = "";
        OnPropertyChanged(nameof(SessionSpanText));
        if (value == null) return;
        for (int i = 0; i < value.ChannelCount; i++)
            Channels.Add(new AnalysisChannelOption
            {
                Index = i,
                Name = i < value.ChannelNames.Length ? value.ChannelNames[i] : $"CH{i}",
                Unit = i < value.ChannelUnits.Length ? value.ChannelUnits[i] : "",
                IsSelected = i == 0,
            });
    }

    public string SessionSpanText => SelectedSession is { EndedAt: { } end } s
        ? $"{s.StartedAt:yyyy-MM-dd HH:mm:ss} — {end:HH:mm:ss}，时长 {FormatDuration(end - s.StartedAt)}，{s.TotalReadings:N0} 条"
        : "";

    [RelayCommand]
    private void SelectAllChannels()
    {
        var target = Channels.Any(c => !c.IsSelected);
        foreach (var c in Channels) c.IsSelected = target;
    }

    private bool CanRun() => !IsBusy && SelectedSession != null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (SelectedSession is not { EndedAt: { } sessionEnd } session) return;
        var channels = Channels.Where(c => c.IsSelected).ToArray();
        if (channels.Length == 0) { Report("请至少选择一个通道。", true); return; }
        if (!TryReadOffset(RangeStartText, out var startOffset) || !TryReadOffset(RangeEndText, out var endOffset))
        {
            Report("时间段请填写相对会话开始的秒数，留空表示从头 / 到尾。", true);
            return;
        }
        var options = new DriftNoiseOptions
        {
            NoiseWindowSeconds = NoiseWindowSeconds,
            DetrendNoiseWindows = DetrendNoiseWindows,
            DriftSegmentSeconds = DriftSegmentSeconds,
        };
        // “Infinity” 会被绑定转换为正无穷，必须一并拒绝，否则窗口切分无法前进。
        if (!double.IsFinite(options.NoiseWindowSeconds) || !(options.NoiseWindowSeconds > 0)
            || !double.IsFinite(options.DriftSegmentSeconds) || !(options.DriftSegmentSeconds > 0))
        {
            Report("噪声窗口和漂移分段必须为有限正数。", true);
            return;
        }

        DateTime from, to;
        try
        {
            from = startOffset is { } a ? session.StartedAt.AddSeconds(a) : session.StartedAt;
            to = endOffset is { } b ? session.StartedAt.AddSeconds(b) : sessionEnd;
        }
        catch (ArgumentOutOfRangeException)
        {
            // 有限但极大的秒数（如 1e300）超出 DateTime 范围：按输入错误提示，不让命令异常结束。
            Report("时间段超出可表示的时间范围，请填写相对会话开始的秒数。", true);
            return;
        }
        if (to <= from) { Report("结束时间必须晚于开始时间。", true); return; }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Results.Clear();
        FocusedResult = null;
        try
        {
            var maxPoints = Math.Max(1, MaxAnalysisValues / (1 + channels.Length));
            var (times, columns) = await LoadAsync(session.Id, from, to, channels.Select(c => c.Index).ToArray(), maxPoints, token);
            if (times.Count > maxPoints)
            {
                Report($"所选时间段超过 {maxPoints:N0} 个时间点（{channels.Length} 个通道时的上限），请缩短时间段或减少通道后再分析。", true);
                return;
            }
            if (times.Count == 0)
            {
                Report("所选时间段内没有数据。", true);
                return;
            }
            Report($"正在计算 {channels.Length} 个通道…", false);
            // 各通道共用同一组相对时间。
            var seconds = times.Select(t => (t - times[0]).TotalSeconds).ToArray();
            var rows = await Task.Run(() => channels.Select((c, k) =>
            {
                token.ThrowIfCancellationRequested();
                var result = DriftNoiseAnalyzer.Analyze(times, columns[k], options, token);
                return new AnalysisResultRow { Channel = c, Result = result, Seconds = seconds, Values = columns[k] };
            }).ToArray(), token);
            foreach (var row in rows) Results.Add(row);
            FocusedResult = Results.FirstOrDefault();
            AnalysisSettingsText = $"会话：{session.Name}；时间段：{from:yyyy-MM-dd HH:mm:ss.fff} — {to:yyyy-MM-dd HH:mm:ss.fff}；"
                + $"数据：原始值（协议解析后、校正前）；噪声窗口 {options.NoiseWindowSeconds:0.###} s，"
                + (options.DetrendNoiseWindows ? "窗口内去线性趋势" : "窗口内仅去均值")
                + $"；漂移分段 {options.DriftSegmentSeconds:0.###} s";
            Report($"已分析 {times.Count:N0} 个时间点，{rows.Length} 个通道。", false);
        }
        catch (OperationCanceledException) { Report("已取消分析。", false); }
        catch (Exception ex) { Report($"分析失败：{ex.Message}", true); }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 分块读取并只保留所选通道的原始值；按读数 ID 去除块边界的重复行。
    /// 点数超过 <paramref name="maxPoints"/> 时立即停止读取，返回的时间点数会大于上限，由调用方提示。
    /// </summary>
    private async Task<(List<DateTime> Times, double[][] Columns)> LoadAsync(
        string sessionId, DateTime from, DateTime to, int[] indices, long maxPoints, CancellationToken token)
    {
        var times = new List<DateTime>();
        var columns = indices.Select(_ => new List<double>()).ToArray();
        var seen = new HashSet<long>();
        for (var chunkStart = from; chunkStart < to; chunkStart += LoadChunk)
        {
            token.ThrowIfCancellationRequested();
            var chunkEnd = chunkStart + LoadChunk < to ? chunkStart + LoadChunk : to;
            Report($"正在读取 {chunkStart:HH:mm:ss} — {chunkEnd:HH:mm:ss}，已读 {times.Count:N0} 条…", false);
            var readings = await _storage.GetReadingsAsync(sessionId, chunkStart, chunkEnd);
            var lastChunk = new HashSet<long>();
            foreach (var r in readings)
            {
                if (r.Id != 0 && !seen.Add(r.Id)) continue;
                lastChunk.Add(r.Id);
                var raw = r.OriginalChannelValues ?? r.ChannelValues;
                times.Add(r.Timestamp);
                for (int k = 0; k < indices.Length; k++)
                    columns[k].Add(indices[k] < raw.Length ? raw[indices[k]] : double.NaN);
            }
            // 只需记住最近一块的 ID 即可去除边界重复，避免长会话的集合无限增长。
            seen = lastChunk;
            if (times.Count > maxPoints) return (times, []);
        }
        return (times, columns.Select(c => c.ToArray()).ToArray());
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanExport() => !IsBusy && Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出分析结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"analysis_{SelectedSession?.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, BuildCsv(), new UTF8Encoding(true));
            Report($"已导出：{dialog.FileName}", false);
        }
        catch (Exception ex) { Report($"导出失败：{ex.Message}", true); }
    }

    internal string BuildCsv()
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        static string N(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        sb.AppendLine(Q("# " + AnalysisSettingsText));
        sb.AppendLine("通道,单位,有效点数,非有限值,实际时长(s),实际频率(Hz),缺失次数,最长缺失(s),均值,标准差,最小值,最大值,峰峰值,RMS,"
            + "漂移(单位/h),趋势R2,去趋势标准差,分段均值极差,噪声窗口数,噪声标准差中位数,噪声标准差最大值,噪声峰峰值中位数,提示");
        foreach (var row in Results)
        {
            var r = row.Result; var q = r.Quality;
            sb.AppendLine(string.Join(",", Q(row.Name), Q(row.Unit), q.SampleCount, q.NonFiniteCount, N(q.DurationSeconds), N(q.ActualRateHz),
                q.GapCount, N(q.MaxGapSeconds), N(r.Mean), N(r.StdDev), N(r.Min), N(r.Max), N(r.PeakToPeak), N(r.Rms),
                N(r.DriftPerHour), N(r.TrendRSquared), N(r.DetrendedStdDev), N(r.SegmentMeanSpread), r.NoiseWindowCount,
                N(r.NoiseMedianStd), N(r.NoiseMaxStd), N(r.NoiseMedianPeakToPeak), Q(row.WarningText)));
        }
        return sb.ToString();
    }

    private static bool TryReadOffset(string text, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
        if (!double.IsFinite(v) || v < 0) return false;
        seconds = v;
        return true;
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span:mm\\:ss}" : span.ToString(@"mm\:ss");

    private void Report(string message, bool error)
    {
        void Apply() { StatusMessage = message; IsError = error; }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }
}
