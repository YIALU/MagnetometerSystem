using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.App.Helpers;
using ScottPlot;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 视图模式枚举
/// </summary>
public enum ViewMode
{
    Single,
    Multi
}

/// <summary>
/// 实时曲线绘制 ViewModel
/// </summary>
public partial class RealtimeChartViewModel : ObservableObject, IDisposable
{
    private readonly DataBus _dataBus;
    private readonly DispatcherTimer _renderTimer;
    private readonly IUserPreferencesService? _preferencesService;
    public WorkspaceLayoutViewModel WorkspaceLayout { get; } = new();

    // 每个通道的数据缓冲（时间戳 + 值）
    private readonly CircularBuffer<double> _timeBuffer = new(100000);
    private CircularBuffer<double>[] _channelBuffers;
    private CircularBuffer<double>[] _rawChannelBuffers = [];
    private readonly object _dataLock = new();

    /// <summary>每通道环形缓冲的容量（点数）</summary>
    private const int ChannelBufferCapacity = 100000;

    private int _channelCount;
    private string[] _channelNames = [];
    private string[] _channelUnits = [];
    private readonly Dictionary<string, ScottPlot.IYAxis> _unitAxes = new();
    private int _layoutRefreshPending;
    private DateTime _startTime;
    private bool _isAcquiring;
    private bool _disposed;
    private sealed record PlotDataSnapshot(double[] Times, double[][] Channels, double[][] Raw, int TotalCount);
    private PlotDataSnapshot? _pausedData;

    // ---- 图表设置 ----

    [ObservableProperty]
    private bool _autoScaleY = true;

    [ObservableProperty]
    private double _yMin = 49000;

    [ObservableProperty]
    private double _yMax = 51000;

    [ObservableProperty]
    private double _timeWindowSeconds = 30;

    public double[] TimeWindowOptions { get; } = [5, 10, 30, 60, 300, 0];

    [ObservableProperty]
    private int _refreshRate = 30;

    [ObservableProperty]
    private bool _showGrid = true;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _isPaused;

    partial void OnIsPausedChanged(bool value)
    {
        _pausedData = value ? CapturePlotData(includeAll: true) : null;
    }

    [ObservableProperty]
    private string _statisticsText = "";

    /// <summary>滚动统计表（原始值）；最多每 0.5 s 更新一次，避免列表随绘图刷新频繁重建。</summary>
    [ObservableProperty]
    private IReadOnlyList<LiveStatisticsRow> _statisticsRows = [];
    private DateTime _lastStatisticsRowsUpdate = DateTime.MinValue;


    [ObservableProperty]
    private string _computationError = "";

    [ObservableProperty]
    private long _dataPointCount;

    // 每通道显示配置（偏移、颜色、可见性）
    [ObservableProperty]
    private ObservableCollection<ChannelDisplayConfig> _channelConfigs = new();

    // 自定义计算通道
    [ObservableProperty]
    private ObservableCollection<ComputedChannelDefinition> _computedChannels = new();

    // 缓存已编译的公式求值器
    private readonly Dictionary<string, FormulaEvaluator> _formulaCache = new();

    // 统计配置
    [ObservableProperty]
    private StatisticsConfig _statisticsConfig = new();

    /// <summary>降采样目标点数（0 = 禁用降采样）</summary>
    [ObservableProperty]
    private int _downsampleTargetCount = 2000;

    // ---- 区间分析 ----

    [ObservableProperty]
    private IntervalSelection? _currentInterval;

    [ObservableProperty]
    private IntervalStatisticsResult? _intervalStatistics;

    [ObservableProperty]
    private bool _isIntervalSelectionMode;

    [ObservableProperty]
    private string _intervalStartInput = "";

    [ObservableProperty]
    private string _intervalEndInput = "";

    [ObservableProperty]
    private string _intervalStatisticsText = "";

    // ---- 滤波设置 ----

    private bool _isFilterEnabled;
    public bool IsFilterEnabled
    {
        get => _isFilterEnabled;
        set => SetProperty(ref _isFilterEnabled, value);
    }

    private int _filterWindowSize = 5;
    public int FilterWindowSize
    {
        get => _filterWindowSize;
        set => SetProperty(ref _filterWindowSize, value);
    }

    private FilterType _selectedFilterType = FilterType.MovingAverage;
    public FilterType SelectedFilterType
    {
        get => _selectedFilterType;
        set => SetProperty(ref _selectedFilterType, value);
    }

    public FilterType[] FilterTypes { get; } = Enum.GetValues<FilterType>();

    // 滤波处理器实例
    private readonly DataProcessor _dataProcessor = new();

    // ---- 多图表模式 ----
    [ObservableProperty]
    private bool _isMultiPlotMode;

    [ObservableProperty]
    private int _multiPlotColumnCount = 1;

    [ObservableProperty]
    private ViewMode _currentViewMode = ViewMode.Single;

    [ObservableProperty]
    private double _singlePlotHeight = 400;

    [ObservableProperty]
    private double _multiPlotHeight = 200;
    [ObservableProperty]
    private bool _isChartHeightAutomatic = true;

    partial void OnCurrentViewModeChanged(ViewMode value) => IsMultiPlotMode = value == ViewMode.Multi;
    partial void OnIsMultiPlotModeChanged(bool value) => CurrentViewMode = value ? ViewMode.Multi : ViewMode.Single;
    partial void OnSinglePlotHeightChanged(double value) => IsChartHeightAutomatic = false;
    partial void OnMultiPlotHeightChanged(double value) => IsChartHeightAutomatic = false;

    // 多图表控件引用（由 View 的 code-behind 设置）
    public List<ScottPlot.WPF.WpfPlot> MultiPlotControls { get; set; } = new();

    // ScottPlot 控件引用（由 View 设置）
    public ScottPlot.WPF.WpfPlot? PlotControl { get; set; }

    // ---- 计算通道向导 ----

    [ObservableProperty]
    private bool _isAddingTotalField;

    [ObservableProperty]
    private bool _isAddingGradient;

    [ObservableProperty]
    private int _wizardSourceA;

    [ObservableProperty]
    private int _wizardSourceB = 1;

    [ObservableProperty]
    private int _wizardSourceC = 2;

    /// <summary>向导可选的原始通道列表</summary>
    [ObservableProperty]
    private ObservableCollection<SourceOption> _wizardRawSources = new();

    /// <summary>向导可选的梯度源列表（原始通道 + 已有计算通道）</summary>
    [ObservableProperty]
    private ObservableCollection<SourceOption> _wizardGradientSources = new();

    /// <summary>梯度基线距离 (m)，用于将差值转换为梯度值 (nT/m)</summary>
    private double _gradientBaselineDistance = 1.0;
    public double GradientBaselineDistance
    {
        get => _gradientBaselineDistance;
        set => SetProperty(ref _gradientBaselineDistance, value);
    }

    public RealtimeChartViewModel(DataBus dataBus, IUserPreferencesService? preferencesService = null)
    {
        _dataBus = dataBus;
        _preferencesService = preferencesService;

        // 通道缓冲按实际协议通道数惰性分配（见 EnsureChannelBuffers），
        // 这里先建一个最小实例，避免其余代码面对 null。
        _channelBuffers = [];

        _dataBus.ProcessedReadingReceived += OnReadingReceived;
        _dataBus.AcquisitionStarted += OnAcquisitionStarted;
        _dataBus.AcquisitionStopped += OnAcquisitionStopped;

        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / _refreshRate),
        };
        _renderTimer.Tick += OnRenderTick;

        ChannelConfigs.CollectionChanged += OnOffsetSourcesChanged;
        ComputedChannels.CollectionChanged += OnOffsetSourcesChanged;
    }

    partial void OnRefreshRateChanged(int value)
    {
        if (value > 0)
            _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / value);
    }

    /// <summary>
    /// 确保通道缓冲至少覆盖 <paramref name="required"/> 个通道。
    /// 已有缓冲原样保留（避免丢掉正在显示的数据），只补齐缺少的部分。
    /// 调用方需持有 _dataLock。
    /// </summary>
    private void EnsureChannelBuffers(int required)
    {
        if (required <= _channelBuffers.Length)
            return;

        var grown = new CircularBuffer<double>[required];
        Array.Copy(_channelBuffers, grown, _channelBuffers.Length);
        for (int i = _channelBuffers.Length; i < required; i++)
            grown[i] = new CircularBuffer<double>(ChannelBufferCapacity);
        _channelBuffers = grown;
        var raw = new CircularBuffer<double>[required];
        Array.Copy(_rawChannelBuffers, raw, _rawChannelBuffers.Length);
        for (int i = _rawChannelBuffers.Length; i < required; i++)
            raw[i] = new CircularBuffer<double>(ChannelBufferCapacity);
        _rawChannelBuffers = raw;
    }

    private void OnAcquisitionStarted(SensorConfig config)
    {
        int channelCount = config.ChannelCount;
        string[] channelNames = config.ChannelNames;
        bool unitsChanged = !_channelUnits.SequenceEqual(config.ChannelUnits);
        _startTime = DateTime.Now;
        _isAcquiring = true;

        lock (_dataLock)
        {
            _channelCount = channelCount;
            _channelNames = channelNames;
            _channelUnits = config.ChannelUnits;
            EnsureChannelBuffers(channelCount);
            _timeBuffer.Clear();
            for (int i = 0; i < _channelBuffers.Length; i++)
            {
                _channelBuffers[i].Clear();
                _rawChannelBuffers[i].Clear();
            }
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DataPointCount = 0;
            IsPaused = false;

            // 初始化通道显示配置：仅当通道数量或名称发生变化时才重建，否则保留现有 Visible 等用户配置
            bool channelLayoutChanged = unitsChanged ||
                ChannelConfigs.Count != _channelCount ||
                !Enumerable.Range(0, _channelCount).All(i =>
                    i < ChannelConfigs.Count &&
                    i < _channelNames.Length &&
                    ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == i) is { } cfg &&
                    cfg.Name == _channelNames[i] && cfg.Unit == _channelUnits.ElementAtOrDefault(i));

            if (channelLayoutChanged)
            {
                ChannelConfigs.Clear();
                var defaults = ChannelDisplayConfig.CreateDefaults(_channelCount, _channelNames);
                foreach (var cfg in defaults)
                {
                    cfg.Unit = _channelUnits.ElementAtOrDefault(cfg.ChannelIndex) ?? "";
                    ChannelConfigs.Add(cfg);
                }

                // 通道布局改变时才清空计算通道（通道引用可能无效）
                ComputedChannels.Clear();
                _formulaCache.Clear();
            }
            else
            {
                // 通道布局未变，仅清空公式缓存以便下一 session 重新求值
                _formulaCache.Clear();
            }

            // 关闭向导面板
            IsAddingTotalField = false;
            IsAddingGradient = false;

            SetupPlot();
            _renderTimer.Start();

            // 恢复图表顺序
            _ = LoadChartOrderAsync();
        });
    }

    private void OnAcquisitionStopped()
    {
        _isAcquiring = false;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshPlot();
            _renderTimer.Stop();
        });
    }

    private void OnReadingReceived(MagnetometerReading reading)
    {
        if (!_isAcquiring) return;

        lock (_dataLock)
        {
            if (_timeBuffer.Count == 0) _startTime = reading.Timestamp;
            var elapsed = (reading.Timestamp - _startTime).TotalSeconds;
            // 正常路径下缓冲已由 OnAcquisitionStarted 按协议通道数备好。
            // 这里兜住"读数先于采集开始事件到达"或"实际通道数多于配置"的情况：
            // 补齐缓冲，并把新缓冲填到与时间轴等长，避免通道间错位。
            int needed = reading.ChannelValues.Length;
            if (needed > _channelBuffers.Length)
            {
                int previous = _channelBuffers.Length;
                EnsureChannelBuffers(needed);
                for (int i = previous; i < needed; i++)
                    for (int pad = 0; pad < _timeBuffer.Count; pad++)
                    {
                        _channelBuffers[i].Add(double.NaN);
                        _rawChannelBuffers[i].Add(double.NaN);
                    }
            }

            if (needed > _channelCount)
            {
                _channelCount = needed;
                _channelNames = Enumerable.Range(0, needed).Select(i => _channelNames.ElementAtOrDefault(i) ?? $"CH{i}").ToArray();
                _channelUnits = Enumerable.Range(0, needed).Select(i => _channelUnits.ElementAtOrDefault(i) ?? "").ToArray();
                if (Interlocked.Exchange(ref _layoutRefreshPending, 1) == 0)
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var cfg in ChannelDisplayConfig.CreateDefaults(_channelCount, _channelNames))
                            if (!ChannelConfigs.Any(c => c.ChannelIndex == cfg.ChannelIndex))
                            { cfg.Unit = _channelUnits[cfg.ChannelIndex]; ChannelConfigs.Add(cfg); }
                        Interlocked.Exchange(ref _layoutRefreshPending, 0);
                    });
            }

            _timeBuffer.Add(elapsed);
            var rawValues = reading.OriginalChannelValues ?? reading.ChannelValues;
            for (int i = 0; i < _channelBuffers.Length; i++)
            {
                _channelBuffers[i].Add(i < reading.ChannelValues.Length ? reading.ChannelValues[i] : double.NaN);
                _rawChannelBuffers[i].Add(i < rawValues.Length ? rawValues[i] : double.NaN);
            }
        }
    }

    public void RefreshPlot() => OnRenderTick(null, EventArgs.Empty);

    private PlotDataSnapshot CapturePlotData(bool includeAll = false)
    {
        lock (_dataLock)
        {
            int totalCount = _timeBuffer.Count;
            int start = 0;
            // Statistics with a zero window follows the plot. An unlimited plot needs all retained data.
            if (!includeAll && totalCount > 0 && TimeWindowSeconds > 0)
            {
                double window = StatisticsConfig.WindowSeconds > 0
                    ? Math.Max(TimeWindowSeconds, StatisticsConfig.WindowSeconds) : TimeWindowSeconds;
                double minimumTime = _timeBuffer[totalCount - 1] - window;
                for (int i = totalCount - 1; i >= 0; i--)
                {
                    if (_timeBuffer[i] < minimumTime) { start = i + 1; break; }
                }
                start = Math.Min(start, totalCount - 1);
            }
            int count = totalCount - start;
            // Copy only the required logical range, including after the circular buffers wrap.
            // Keep every source channel: hidden channels can still feed computed channels/statistics.
            double[] CopyRange(CircularBuffer<double> buffer)
            {
                var values = new double[count];
                for (int i = 0; i < count; i++)
                    values[i] = start + i < buffer.Count ? buffer[start + i] : double.NaN;
                return values;
            }
            var channels = new double[_channelCount][];
            var raw = new double[_channelCount][];
            for (int ch = 0; ch < _channelCount; ch++)
            {
                channels[ch] = CopyRange(_channelBuffers[ch]);
                raw[ch] = CopyRange(_rawChannelBuffers[ch]);
            }
            return new PlotDataSnapshot(CopyRange(_timeBuffer), channels, raw, totalCount);
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (IsPaused && sender is not null) return;
        var (times, channelData, rawData, totalCount) = _pausedData ?? CapturePlotData();
        if (channelData.Length < _channelCount)
        {
            channelData = Enumerable.Range(0, _channelCount).Select(i => i < channelData.Length
                ? channelData[i] : Enumerable.Repeat(double.NaN, times.Length).ToArray()).ToArray();
            rawData = Enumerable.Range(0, _channelCount).Select(i => i < rawData.Length
                ? rawData[i] : Enumerable.Repeat(double.NaN, times.Length).ToArray()).ToArray();
        }

        DataPointCount = totalCount;
        _lastFrame = (times, rawData);
        if (times.Length == 0)
        {
            if (!IsMultiPlotMode && PlotControl != null) PlotControl.Refresh();
            return;
        }

        foreach (var cfg in ChannelConfigs)
            if (cfg.ChannelIndex < rawData.Length && rawData[cfg.ChannelIndex].Length > 0)
                cfg.LatestValue = $"{rawData[cfg.ChannelIndex][^1]:G8} {cfg.Unit}";

        double xMax = times[^1];
        double xMin = TimeWindowSeconds > 0 ? xMax - TimeWindowSeconds : times[0];

        int startIdx = WindowStartIndex(times);
        int count = times.Length - startIdx;
        var windowTimes = times.AsSpan(startIdx, count).ToArray();

        if (IsMultiPlotMode)
        {
            RenderMultiPlot(windowTimes, channelData, startIdx, count, xMin, xMax);
        }
        else
        {
            RenderSinglePlot(windowTimes, channelData, startIdx, count, xMin, xMax);
        }

        UpdateStatistics(times, rawData, startIdx, count);
    }

    /// <summary>当前时间窗口在缓冲中的起点（窗口为 0 表示全部）。</summary>
    private int WindowStartIndex(double[] times)
    {
        if (TimeWindowSeconds <= 0 || times.Length == 0) return 0;
        double xMin = times[^1] - TimeWindowSeconds;
        for (int i = times.Length - 1; i >= 0; i--)
            if (times[i] < xMin) return Math.Min(i + 1, times.Length - 1);
        return 0;
    }

    private void RenderSinglePlot(double[] windowTimes, double[][] channelData,
        int startIdx, int count, double xMin, double xMax)
    {
        if (PlotControl == null) return;

        var plot = PlotControl.Plot;
        plot.Clear();
        ConfigureUnitAxes(plot);

        // 绘制各通道
        for (int ch = 0; ch < _channelCount; ch++)
        {
            var config = ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == ch);
            if (config != null && !config.Visible)
                continue;

            if (ch >= channelData.Length || channelData[ch].Length < startIdx + count)
                continue;

            var windowValues = channelData[ch].AsSpan(startIdx, count).ToArray();

            if (config != null && config.DisplayOffset != 0)
            {
                for (int i = 0; i < windowValues.Length; i++)
                    windowValues[i] += config.DisplayOffset;
            }

            // 应用滤波
            windowValues = ApplyFilter(windowValues);

            var (plotXs, plotYs) = ApplyDownsampling(windowTimes, windowValues);
            var sig = plot.Add.ScatterLine(plotXs, plotYs);
            if (config is not null && _unitAxes.TryGetValue(config.Unit, out var axis))
                sig.Axes.YAxis = axis;

            if (config != null)
            {
                var (a, r, g, b) = config.ParseColor();
                sig.Color = new ScottPlot.Color(r, g, b, a);
            }

            sig.LineWidth = 1.5f;
            sig.LegendText = (config?.Name ?? $"CH{ch}") + $" ({config?.Unit})";
        }

        // 绘制计算通道
        RenderComputedChannels(plot, windowTimes, channelData, startIdx, count);

        ConfigurePlotAxes(plot, xMin, xMax);
        // 无参 AutoScaleY 只调整左轴；独立单位轴始终按自身数据确定范围。
        foreach (var axis in _unitAxes.Values.Where(a => !ReferenceEquals(a, plot.Axes.Left)))
            plot.Axes.AutoScaleY(axis);
        plot.ShowLegend();
        AddOverlays(plot, null);
        PlotControl.Refresh();
    }

    private void ConfigureUnitAxes(ScottPlot.Plot plot)
    {
        foreach (var old in _unitAxes.Values.Where(a => !ReferenceEquals(a, plot.Axes.Left)))
            plot.Axes.Remove(old);
        _unitAxes.Clear();
        var units = ChannelConfigs.Where(c => c.Visible).Select(c => c.Unit)
            .Concat(ComputedChannels.Where(c => c.Enabled).Select(c => c.Unit))
            .Distinct().OrderBy(u => u is "°C" or "℃" ? 1 : 0).ToArray();
        plot.Axes.Left.Label.Text = units.FirstOrDefault() ?? "数值";
        foreach (var unit in units)
        {
            ScottPlot.IYAxis axis = _unitAxes.Count == 0 ? plot.Axes.Left : plot.Axes.AddRightAxis();
            axis.Label.Text = unit;
            _unitAxes.Add(unit, axis);
        }
    }

    private void RenderMultiPlot(double[] windowTimes, double[][] channelData,
        int startIdx, int count, double xMin, double xMax)
    {
        int plotIdx = 0;

        // 修复拖拽错位：按 ChannelConfigs 顺序迭代，用 config.ChannelIndex 取物理通道数据
        foreach (var config in ChannelConfigs)
        {
            if (!config.Visible) continue;

            int ch = config.ChannelIndex;
            if (ch >= _channelCount || plotIdx >= MultiPlotControls.Count) break;

            var plotCtrl = MultiPlotControls[plotIdx];
            var plot = plotCtrl.Plot;
            plot.Clear();

            if (ch < channelData.Length && channelData[ch].Length >= startIdx + count)
            {
                var windowValues = channelData[ch].AsSpan(startIdx, count).ToArray();

                if (config.DisplayOffset != 0)
                {
                    for (int i = 0; i < windowValues.Length; i++)
                        windowValues[i] += config.DisplayOffset;
                }

                // 应用滤波
                windowValues = ApplyFilter(windowValues);

                var (plotXs, plotYs) = ApplyDownsampling(windowTimes, windowValues);
                var sig = plot.Add.ScatterLine(plotXs, plotYs);
                var (a, r, g, b) = config.ParseColor();
                sig.Color = new ScottPlot.Color(r, g, b, a);
                sig.LineWidth = 1.5f;

                // 图上统计标注（右上角）
                var stat = StatisticsResultItem.Compute(config.Name, windowValues);
                var ann = plot.Add.Annotation("显示窗口\n" + stat.FormatMultiline(), ScottPlot.Alignment.UpperRight);
                ann.LabelFontSize = 10;
                ann.LabelFontName = ChartFontHelper.DefaultCjkFont;
                ann.LabelBackgroundColor = new ScottPlot.Color(255, 255, 255, 200);
                ann.LabelBorderColor = new ScottPlot.Color(200, 200, 200, 255);
                ann.LabelBorderWidth = 1;
            }

            plot.Axes.Left.Label.Text = $"{config.Name} ({config.Unit})";
            ConfigurePlotAxes(plot, xMin, xMax);
            AddOverlays(plot, config);
            plotCtrl.Refresh();
            plotIdx++;
        }

        // 绘制计算通道
        foreach (var computed in ComputedChannels)
        {
            if (!computed.Enabled || string.IsNullOrWhiteSpace(computed.Formula))
                continue;

            if (plotIdx >= MultiPlotControls.Count) break;

            var evaluator = GetOrCreateEvaluator(computed.Formula);
            if (evaluator == null) continue;

            var plotCtrl = MultiPlotControls[plotIdx];
            var plot = plotCtrl.Plot;
            plot.Clear();

            var computedValues = new double[count];
            for (int i = 0; i < count; i++)
            {
                var chVals = new double[channelData.Length];
                for (int ch = 0; ch < chVals.Length; ch++)
                {
                    if (channelData[ch].Length > startIdx + i)
                        chVals[ch] = channelData[ch][startIdx + i];
                }
                computedValues[i] = evaluator.Evaluate(chVals);
            }

            if (computed.DisplayOffset != 0)
            {
                for (int i = 0; i < computedValues.Length; i++)
                    computedValues[i] += computed.DisplayOffset;
            }

            computedValues = ApplyFilter(computedValues);

            var (plotXs, plotYs) = ApplyDownsampling(windowTimes, computedValues);
            var compSig = plot.Add.ScatterLine(plotXs, plotYs);
            var (ca, cr, cg, cb) = new ChannelDisplayConfig { ColorHex = computed.ColorHex }.ParseColor();
            compSig.Color = new ScottPlot.Color(cr, cg, cb, ca);
            compSig.LineWidth = computed.LineWidth;

            // 计算通道统计标注
            var compStat = StatisticsResultItem.Compute(computed.Name, computedValues);
            var compAnn = plot.Add.Annotation("显示窗口\n" + compStat.FormatMultiline(), ScottPlot.Alignment.UpperRight);
            compAnn.LabelFontSize = 10;
            compAnn.LabelFontName = ChartFontHelper.DefaultCjkFont;
            compAnn.LabelBackgroundColor = new ScottPlot.Color(255, 255, 255, 200);
            compAnn.LabelBorderColor = new ScottPlot.Color(200, 200, 200, 255);
            compAnn.LabelBorderWidth = 1;

            plot.Axes.Left.Label.Text = $"{computed.Name} ({computed.Unit})";
            ConfigurePlotAxes(plot, xMin, xMax);
            AddOverlays(plot, null);
            plotCtrl.Refresh();
            plotIdx++;
        }
    }

    private void RenderComputedChannels(ScottPlot.Plot plot, double[] windowTimes,
        double[][] channelData, int startIdx, int count)
    {
        foreach (var computed in ComputedChannels)
        {
            if (!computed.Enabled || string.IsNullOrWhiteSpace(computed.Formula))
                continue;

            var evaluator = GetOrCreateEvaluator(computed.Formula);
            if (evaluator == null) continue;

            var computedValues = new double[count];
            for (int i = 0; i < count; i++)
            {
                var chVals = new double[channelData.Length];
                for (int ch = 0; ch < chVals.Length; ch++)
                {
                    if (channelData[ch].Length > startIdx + i)
                        chVals[ch] = channelData[ch][startIdx + i];
                }
                computedValues[i] = evaluator.Evaluate(chVals);
            }

            // 应用显示偏移
            if (computed.DisplayOffset != 0)
            {
                for (int i = 0; i < computedValues.Length; i++)
                    computedValues[i] += computed.DisplayOffset;
            }

            // 应用滤波
            computedValues = ApplyFilter(computedValues);

            var (plotXs, plotYs) = ApplyDownsampling(windowTimes, computedValues);
            var compSig = plot.Add.ScatterLine(plotXs, plotYs);
            var (ca, cr, cg, cb) = new ChannelDisplayConfig { ColorHex = computed.ColorHex }.ParseColor();
            compSig.Color = new ScottPlot.Color(cr, cg, cb, ca);
            compSig.LineWidth = computed.LineWidth;
            compSig.LegendText = computed.Name;
            if (_unitAxes.TryGetValue(computed.Unit, out var axis)) compSig.Axes.YAxis = axis;
        }
    }

    /// <summary>
    /// 根据降采样策略处理窗口数据
    /// </summary>
    private (double[] xs, double[] ys) ApplyDownsampling(double[] windowTimes, double[] windowValues)
    {
        if (DownsampleTargetCount <= 0 || windowTimes.Length <= DownsampleTargetCount)
            return (windowTimes, windowValues);

        return LttbDownsampler.Downsample(windowTimes, windowValues, DownsampleTargetCount);
    }

    /// <summary>
    /// 根据滤波设置处理数据
    /// </summary>
    private double[] ApplyFilter(double[] values)
    {
        if (!IsFilterEnabled || FilterWindowSize <= 1 || values.Length == 0)
            return values;

        return SelectedFilterType switch
        {
            FilterType.MovingAverage => _dataProcessor.MovingAverage(values, FilterWindowSize),
            FilterType.Median => _dataProcessor.MedianFilter(values, FilterWindowSize),
            _ => values
        };
    }

    private void ConfigurePlotAxes(ScottPlot.Plot plot, double xMin, double xMax)
    {
        if (AutoScroll)
            plot.Axes.SetLimitsX(xMin, xMax);

        if (AutoScaleY)
            plot.Axes.AutoScaleY();
        else
            plot.Axes.SetLimitsY(YMin, YMax);

        plot.Axes.Bottom.Label.Text = "时间 (s)";
        plot.Grid.IsVisible = ShowGrid;
    }

    private void UpdateStatistics(double[] times, double[][] channelData, int startIdx, int count)
    {
        if (count <= 0 || _channelCount <= 0) { StatisticsText = ""; StatisticsRows = []; return; }

        // 确定统计窗口
        var statConfig = StatisticsConfig;
        int statStartIdx = startIdx;
        int statCount = count;

        if (statConfig.WindowSeconds > 0 && times.Length > 0)
        {
            double statXMin = times[^1] - statConfig.WindowSeconds;
            statStartIdx = 0;
            for (int i = times.Length - 1; i >= 0; i--)
            {
                if (times[i] < statXMin) { statStartIdx = i + 1; break; }
            }
            statStartIdx = Math.Min(statStartIdx, times.Length - 1);
            statCount = times.Length - statStartIdx;
        }

        var lines = new List<string>();
        var rows = new List<LiveStatisticsRow>();
        for (int ch = 0; ch < _channelCount; ch++)
        {
            if (ch >= channelData.Length || channelData[ch].Length < statStartIdx + statCount)
                continue;

            var span = channelData[ch].AsSpan(statStartIdx, statCount).ToArray().Where(double.IsFinite).ToArray();
            if (span.Length == 0) continue;
            string name = ch < _channelNames.Length ? _channelNames[ch] : $"CH{ch}";
            var result = StatisticsResultItem.Compute(name, span);
            lines.Add(result.Format(statConfig));
            rows.Add(new LiveStatisticsRow(result, _channelUnits.ElementAtOrDefault(ch) ?? "", span.Length));
        }
        var now = DateTime.UtcNow;
        if ((now - _lastStatisticsRowsUpdate).TotalMilliseconds >= 500 || rows.Count != StatisticsRows.Count)
        {
            _lastStatisticsRowsUpdate = now;
            StatisticsRows = rows;
        }
        StatisticsText = "原始数据  ·  " + string.Join("  |  ", lines);
    }

    private FormulaEvaluator? GetOrCreateEvaluator(string formula)
    {
        if (_formulaCache.TryGetValue(formula, out var cached))
            return cached;

        try
        {
            var eval = new FormulaEvaluator(formula);
            _formulaCache[formula] = eval;
            return eval;
        }
        catch
        {
            return null;
        }
    }

    private void SetupPlot()
    {
        if (PlotControl == null) return;
        var plot = PlotControl.Plot;
        plot.Clear();
        plot.Axes.Bottom.Label.Text = "时间 (s)";
        plot.Axes.Left.Label.Text = "数值（单位由协议定义）";
        PlotControl.Refresh();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
    }

    [RelayCommand]
    private void ToggleMultiPlotMode()
    {
        IsMultiPlotMode = !IsMultiPlotMode;
        CurrentViewMode = IsMultiPlotMode ? ViewMode.Multi : ViewMode.Single;
    }

    [RelayCommand]
    private void ToggleColumnCount()
    {
        MultiPlotColumnCount = MultiPlotColumnCount == 1 ? 2 : 1;
    }

    [RelayCommand]
    private void ToggleChannel(int channelIndex)
    {
        var config = ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == channelIndex);
        if (config is not null) config.Visible = !config.Visible;
    }

    [RelayCommand]
    private void ClearChart()
    {
        _lastFrame = null;
        _hoverTime = null;
        lock (_dataLock)
        {
            _timeBuffer.Clear();
            for (int i = 0; i < _channelBuffers.Length; i++)
            {
                _channelBuffers[i].Clear();
                _rawChannelBuffers[i].Clear();
            }
        }
        DataPointCount = 0;
        StatisticsText = "暂无数据";
        StatisticsRows = [];
        ClearIntervalSelection();
        _pausedData = IsPaused ? CapturePlotData(includeAll: true) : null;
        foreach (var config in ChannelConfigs) config.LatestValue = "—";
        foreach (var control in MultiPlotControls)
        {
            control.Plot.Clear();
            control.Refresh();
        }

        if (PlotControl != null)
        {
            PlotControl.Plot.Clear();
            PlotControl.Refresh();
        }
    }

    public void ZoomTimeWindow(double factor)
    {
        if (TimeWindowSeconds <= 0) return;
        var newVal = TimeWindowSeconds * factor;
        TimeWindowSeconds = Math.Clamp(newVal, 1, 3600);
    }

    public void ZoomYAxis(double factor)
    {
        if (AutoScaleY) return;
        var center = (YMin + YMax) / 2;
        var halfRange = (YMax - YMin) / 2 * factor;
        YMin = center - halfRange;
        YMax = center + halfRange;
    }

    // ---- 计算通道操作 ----

    [RelayCommand]
    private void AddComputedChannel()
    {
        string defaultFormula = "CH0";
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Calc{ComputedChannels.Count}",
            Formula = defaultFormula,
            ChannelType = ComputedChannelType.Custom,
            ColorHex = "#FF000000",
        });
        IsAddingTotalField = false;
        IsAddingGradient = false;
    }

    [RelayCommand]
    private void RemoveComputedChannel(ComputedChannelDefinition? def)
    {
        if (def != null)
        {
            ComputedChannels.Remove(def);
            _formulaCache.Remove(def.Formula);
        }
    }

    // ---- 总场向导 ----

    [RelayCommand]
    private void StartAddTotalField()
    {
        ComputationError = "";
        BuildWizardRawSources();
        WizardSourceA = 0;
        WizardSourceB = Math.Min(1, WizardRawSources.Count - 1);
        WizardSourceC = Math.Min(2, WizardRawSources.Count - 1);
        IsAddingTotalField = true;
        IsAddingGradient = false;
    }

    [RelayCommand]
    private void ConfirmAddTotalField()
    {
        ComputationError = "";
        if (WizardSourceA < 0 || WizardSourceA >= WizardRawSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardRawSources.Count
            || WizardSourceC < 0 || WizardSourceC >= WizardRawSources.Count)
        {
            IsAddingTotalField = false;
            return;
        }

        var sources = new[] { WizardRawSources[WizardSourceA], WizardRawSources[WizardSourceB], WizardRawSources[WizardSourceC] };
        if (sources.Select(s => s.FormulaExpr).Distinct().Count() != 3 || !HaveSameMagneticUnit(sources))
        { ComputationError = "总场需要三个不同通道，且使用相同的磁场单位。"; return; }
        var a = WizardRawSources[WizardSourceA].FormulaExpr;
        var b = WizardRawSources[WizardSourceB].FormulaExpr;
        var c = WizardRawSources[WizardSourceC].FormulaExpr;
        var formula = $"sqrt({a}*{a} + {b}*{b} + {c}*{c})";

        int totalCount = ComputedChannels.Count(ch => ch.ChannelType == ComputedChannelType.TotalField) + 1;
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Total{totalCount}",
            Unit = sources[0].Unit,
            Formula = formula,
            ChannelType = ComputedChannelType.TotalField,
            ColorHex = "#FF000000",
            LineWidth = 2f,
        });

        IsAddingTotalField = false;
    }

    // ---- 梯度向导 ----

    [RelayCommand]
    private void StartAddGradient()
    {
        ComputationError = "";
        BuildWizardGradientSources();
        WizardSourceA = 0;
        WizardSourceB = Math.Min(1, WizardGradientSources.Count - 1);
        IsAddingTotalField = false;
        IsAddingGradient = true;
    }

    [RelayCommand]
    private void ConfirmAddGradient()
    {
        ComputationError = "";
        if (WizardSourceA < 0 || WizardSourceA >= WizardGradientSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardGradientSources.Count)
        {
            IsAddingGradient = false;
            return;
        }

        var sources = new[] { WizardGradientSources[WizardSourceA], WizardGradientSources[WizardSourceB] };
        if (sources[0].FormulaExpr == sources[1].FormulaExpr || !HaveSameMagneticUnit(sources))
        { ComputationError = "磁场梯度需要两个不同来源，且使用相同的磁场单位。"; return; }
        var a = WizardGradientSources[WizardSourceA].FormulaExpr;
        var b = WizardGradientSources[WizardSourceB].FormulaExpr;
        if (!double.IsFinite(GradientBaselineDistance) || GradientBaselineDistance <= 0)
        { ComputationError = "梯度基线距离必须为有限正数。"; return; }
        var formula = GradientBaselineDistance != 1.0
            ? $"(({a}) - ({b})) / {GradientBaselineDistance.ToString("R", CultureInfo.InvariantCulture)}"
            : $"({a}) - ({b})";

        int gradCount = ComputedChannels.Count(ch => ch.ChannelType == ComputedChannelType.Gradient) + 1;
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Grad{gradCount}",
            Unit = sources[0].Unit + "/m",
            Formula = formula,
            ChannelType = ComputedChannelType.Gradient,
            ColorHex = "#FF808080",
        });

        IsAddingGradient = false;
    }

    partial void OnIsAddingTotalFieldChanged(bool value) => ComputationError = "";
    partial void OnIsAddingGradientChanged(bool value) => ComputationError = "";

    [RelayCommand]
    private void CancelAddWizard()
    {
        ComputationError = "";
        IsAddingTotalField = false;
        IsAddingGradient = false;
    }

    private static bool HaveSameMagneticUnit(SourceOption[] sources) =>
        sources.Select(s => s.Unit).Distinct().Count() == 1
        && sources[0].Unit is "nT" or "uT" or "µT" or "μT" or "mT" or "T";

    /// <summary>
    /// 构建向导可选的原始通道列表
    /// </summary>
    private void BuildWizardRawSources()
    {
        WizardRawSources.Clear();
        for (int i = 0; i < _channelCount; i++)
        {
            var label = i < _channelNames.Length ? _channelNames[i] : $"CH{i}";
            WizardRawSources.Add(new SourceOption { Label = label, FormulaExpr = $"CH{i}", Unit = _channelUnits.ElementAtOrDefault(i) ?? "" });
        }
    }

    /// <summary>
    /// 构建向导可选的梯度源列表（原始通道 + 已有计算通道）
    /// </summary>
    private void BuildWizardGradientSources()
    {
        WizardGradientSources.Clear();

        // 原始通道
        for (int i = 0; i < _channelCount; i++)
        {
            var label = i < _channelNames.Length ? _channelNames[i] : $"CH{i}";
            WizardGradientSources.Add(new SourceOption { Label = label, FormulaExpr = $"CH{i}", Unit = _channelUnits.ElementAtOrDefault(i) ?? "" });
        }

        // 已有计算通道（内联其公式）
        foreach (var comp in ComputedChannels)
        {
            if (!string.IsNullOrWhiteSpace(comp.Formula))
            {
                WizardGradientSources.Add(new SourceOption
                {
                    Label = comp.Name,
                    FormulaExpr = comp.Formula,
                    Unit = comp.Unit,
                });
            }
        }
    }

    // ---- 一键归零 ----

    /// <summary>有通道（含计算通道）设置了显示偏移时为 true，工具栏据此显示“取消归零”。</summary>
    public bool HasDisplayOffsets =>
        ChannelConfigs.Any(c => c.DisplayOffset != 0) || ComputedChannels.Any(c => c.DisplayOffset != 0);

    private readonly HashSet<INotifyPropertyChanged> _offsetSources = new();

    private void OnOffsetSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 通道列表会整体清空重建（Reset 不带旧项），按当前内容重新对齐订阅。
        var current = ChannelConfigs.Cast<INotifyPropertyChanged>().Concat(ComputedChannels).ToHashSet();
        foreach (var gone in _offsetSources.Where(source => !current.Contains(source)).ToList())
        {
            gone.PropertyChanged -= OnOffsetSourcePropertyChanged;
            _offsetSources.Remove(gone);
        }
        foreach (var source in current)
            if (_offsetSources.Add(source)) source.PropertyChanged += OnOffsetSourcePropertyChanged;
        OnPropertyChanged(nameof(HasDisplayOffsets));
    }

    private void OnOffsetSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelDisplayConfig.DisplayOffset))
            OnPropertyChanged(nameof(HasDisplayOffsets));
    }

    private bool CanZeroVisibleChannels() => DataPointCount > 0;

    partial void OnDataPointCountChanged(long value)
    {
        // 绘图每次刷新都会更新点数，只在“有无数据”变化时通知按钮。
        if ((value > 0) != _zeroCommandHadData)
        {
            _zeroCommandHadData = value > 0;
            ZeroVisibleChannelsCommand.NotifyCanExecuteChanged();
        }
    }
    private bool _zeroCommandHadData;

    /// <summary>
    /// 一键归零：把当前显示的每条曲线（可见通道与启用的计算通道）在时间窗口内的均值移到 0。
    /// 只设置显示偏移（暂停时用冻结的数据），不改变保存的原始值、统计表和十字准线读数。
    /// 手动纵轴范围是按原始数值设的，归零后曲线会移出范围，因此主 Y 轴改为自动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanZeroVisibleChannels))]
    private void ZeroVisibleChannels()
    {
        var (times, channelData, _, _) = _pausedData ?? CapturePlotData();
        if (times.Length == 0) return;
        int start = WindowStartIndex(times);
        int count = times.Length - start;

        foreach (var config in ChannelConfigs.Where(c => c.Visible))
        {
            int ch = config.ChannelIndex;
            if (ch < channelData.Length && channelData[ch].Length >= start + count
                && FiniteMean(channelData[ch].AsSpan(start, count)) is { } mean)
                config.DisplayOffset = -mean;
        }

        foreach (var computed in ComputedChannels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Formula)))
        {
            var evaluator = GetOrCreateEvaluator(computed.Formula);
            if (evaluator == null) continue;
            var values = new double[count];
            var row = new double[channelData.Length];
            for (int i = 0; i < count; i++)
            {
                for (int ch = 0; ch < row.Length; ch++)
                    row[ch] = channelData[ch].Length > start + i ? channelData[ch][start + i] : double.NaN;
                values[i] = evaluator.Evaluate(row);
            }
            if (FiniteMean(values) is { } mean) computed.DisplayOffset = -mean;
        }

        AutoScaleY = true;
        RefreshPlot();
    }

    /// <summary>取消归零：清除所有通道与计算通道的显示偏移。</summary>
    [RelayCommand]
    private void ClearDisplayOffsets()
    {
        foreach (var config in ChannelConfigs) config.DisplayOffset = 0;
        foreach (var computed in ComputedChannels) computed.DisplayOffset = 0;
        RefreshPlot();
    }

    private static double? FiniteMean(ReadOnlySpan<double> values)
    {
        double sum = 0;
        int n = 0;
        foreach (var v in values)
            if (double.IsFinite(v)) { sum += v; n++; }
        return n > 0 ? sum / n : null;
    }

    // ---- 自动偏移 ----

    /// <summary>通道颜色可选色块：与默认色相同的 8 色。</summary>
    public static IReadOnlyList<string> ChannelSwatches { get; } = ChannelDisplayConfig.PresetColors;

    [RelayCommand]
    private void ClearChannelOffset(int channelIndex)
    {
        if (ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == channelIndex) is { } config)
            config.DisplayOffset = 0;
    }

    [RelayCommand]
    private void MoveChannelUp(ChannelDisplayConfig? config)
    {
        if (config is null) return;
        var index = ChannelConfigs.IndexOf(config);
        if (index > 0) ReorderChannels(index, index - 1);
    }

    [RelayCommand]
    private void MoveChannelDown(ChannelDisplayConfig? config)
    {
        if (config is null) return;
        var index = ChannelConfigs.IndexOf(config);
        if (index >= 0 && index < ChannelConfigs.Count - 1) ReorderChannels(index, index + 1);
    }

    // ---- 自动偏移（原有） ----

    /// <summary>
    /// 自动偏移：计算指定通道的平均值，设置 DisplayOffset = -average
    /// </summary>
    [RelayCommand]
    private void AutoOffsetChannel(int channelIndex)
    {
        var config = ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == channelIndex);
        if (config is null) return;

        double[] data;
        lock (_dataLock)
        {
            if (channelIndex >= _channelBuffers.Length)
                return;
            data = _channelBuffers[channelIndex].ToArray();
        }

        data = data.Where(double.IsFinite).ToArray();
        if (data.Length == 0) return;
        config.DisplayOffset = -data.Average();
    }

    /// <summary>
    /// 自动偏移计算通道：计算当前数据的平均值，设置 DisplayOffset = -average
    /// </summary>
    [RelayCommand]
    private void AutoOffsetComputedChannel(ComputedChannelDefinition? def)
    {
        if (def == null || string.IsNullOrWhiteSpace(def.Formula))
            return;

        var evaluator = GetOrCreateEvaluator(def.Formula);
        if (evaluator == null) return;

        double[][] channelData;
        lock (_dataLock)
        {
            channelData = new double[_channelCount][];
            for (int i = 0; i < _channelCount; i++)
                channelData[i] = _channelBuffers[i].ToArray();
        }

        int sampleCount = channelData.Length > 0 ? channelData[0].Length : 0;
        if (sampleCount == 0) return;

        double sum = 0;
        int validCount = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            var chVals = new double[channelData.Length];
            for (int ch = 0; ch < chVals.Length; ch++)
            {
                if (channelData[ch].Length > i)
                    chVals[ch] = channelData[ch][i];
            }
            double val = evaluator.Evaluate(chVals);
            if (double.IsFinite(val))
            {
                sum += val;
                validCount++;
            }
        }

        if (validCount > 0)
        {
            def.DisplayOffset = -(sum / validCount);
        }
    }

    // ---- 曲线交互：拖动选区间、十字准线 ----
    // 叠加层只画在曲线上，不影响数据、保存或统计；鼠标移动时只替换叠加层并重绘，不重新复制缓冲。

    private (double[] Times, double[][] Raw)? _lastFrame;
    private double? _dragStart, _dragEnd, _hoverTime;
    private readonly Dictionary<ScottPlot.Plot, (ChannelDisplayConfig? Channel, List<ScottPlot.IPlottable> Items)> _overlays = new();
    private static readonly ScottPlot.Color OverlayAccent = ScottPlot.Color.FromHex("#2456C2");
    private static readonly ScottPlot.Color OverlayMuted = ScottPlot.Color.FromHex("#66726E");

    /// <summary>曲线上鼠标所在时间（采集开始后的秒数）；null 表示鼠标不在曲线上。</summary>
    public double? HoverTime => _hoverTime;

    public void SetHoverTime(double? seconds)
    {
        if (_hoverTime == seconds) return;
        _hoverTime = seconds;
        RequestOverlayRefresh();
    }

    private bool _overlayRefreshPending;

    /// <summary>
    /// 鼠标移动（悬停、拖动预览）的事件远多于绘图刷新：合并为一次叠加层重绘，在界面处理完输入后执行；
    /// 多图模式下不会每次移动都重绘全部图表。
    /// </summary>
    private void RequestOverlayRefresh()
    {
        if (_overlayRefreshPending) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { RefreshOverlays(); return; }
        _overlayRefreshPending = true;
        dispatcher.BeginInvoke(() => { _overlayRefreshPending = false; RefreshOverlays(); }, DispatcherPriority.Background);
    }

    /// <summary>叠加层实际重绘次数（诊断与测试用）。</summary>
    internal int OverlayRefreshCount { get; private set; }

    /// <summary>仍保留叠加层记录的图数量（诊断与测试用）。</summary>
    internal int OverlayPlotCount => _overlays.Count;

    /// <summary>视图卸载或重建多图后调用：丢掉不再显示的图的叠加层记录，避免旧 ScottPlot 对象及其数据被留住。</summary>
    public void ForgetDetachedPlots()
    {
        var live = MultiPlotControls.Select(c => c.Plot).Append(PlotControl?.Plot).ToHashSet();
        foreach (var stale in _overlays.Keys.Where(p => !live.Contains(p)).ToArray()) _overlays.Remove(stale);
    }

    public void BeginPlotSelection(double seconds)
    {
        _dragStart = _dragEnd = seconds;
        RefreshOverlays();
    }

    public void UpdatePlotSelection(double seconds)
    {
        if (_dragStart is null) return;
        _dragEnd = seconds;
        RequestOverlayRefresh();
    }

    public void CancelPlotSelection()
    {
        _dragStart = _dragEnd = null;
        RefreshOverlays();
    }

    /// <summary>结束拖动：按拖过的时间范围填入区间并计算统计。返回是否得到有效区间。</summary>
    public bool EndPlotSelection(double seconds)
    {
        if (_dragStart is not { } start) return false;
        _dragStart = _dragEnd = null;
        var (from, to) = (Math.Min(start, seconds), Math.Max(start, seconds));
        if (_lastFrame is { Times.Length: > 0 } frame)
        {
            from = Math.Max(from, frame.Times[0]);
            to = Math.Min(to, frame.Times[^1]);
        }
        if (!(to > from)) { RefreshOverlays(); return false; }
        IntervalStartInput = from.ToString("0.###", CultureInfo.CurrentCulture);
        IntervalEndInput = to.ToString("0.###", CultureInfo.CurrentCulture);
        ApplyIntervalSelection();
        RefreshOverlays();
        return CurrentInterval != null;
    }

    partial void OnCurrentIntervalChanged(IntervalSelection? value) => RefreshOverlays();

    /// <summary>在一张图上加区间阴影、拖动预览和十字准线读数。channel 为多图模式下这张图对应的通道。</summary>
    private void AddOverlays(ScottPlot.Plot plot, ChannelDisplayConfig? channel)
    {
        var items = new List<ScottPlot.IPlottable>();
        if (CurrentInterval is { } interval)
        {
            var span = plot.Add.VerticalSpan(interval.StartTime, interval.EndTime);
            span.FillStyle.Color = OverlayAccent.WithAlpha(.10);
            span.LineStyle.Color = OverlayAccent.WithAlpha(.45);
            span.LineStyle.Width = 1;
            items.Add(span);
        }
        if (_dragStart is { } a && _dragEnd is { } b && a != b)
        {
            var drag = plot.Add.VerticalSpan(Math.Min(a, b), Math.Max(a, b));
            drag.FillStyle.Color = OverlayAccent.WithAlpha(.22);
            drag.LineStyle.Width = 0;
            items.Add(drag);
        }
        if (_hoverTime is { } t && _lastFrame is { Times.Length: > 0 } frame)
        {
            var line = plot.Add.VerticalLine(t);
            line.Color = OverlayMuted.WithAlpha(.8);
            line.LineWidth = 1;
            line.LinePattern = ScottPlot.LinePattern.Dashed;
            items.Add(line);
            var text = HoverText(frame, t, channel);
            if (text is not null)
            {
                var ann = plot.Add.Annotation(text, channel is null ? ScottPlot.Alignment.UpperRight : ScottPlot.Alignment.LowerLeft);
                ann.LabelFontSize = 11;
                ann.LabelFontName = ChartFontHelper.DefaultCjkFont;
                ann.LabelBackgroundColor = new ScottPlot.Color(255, 255, 255, 225);
                ann.LabelBorderColor = ScottPlot.Color.FromHex("#D2D9D5");
                ann.LabelBorderWidth = 1;
                items.Add(ann);
            }
        }
        _overlays[plot] = (channel, items);
    }

    /// <summary>十字准线读数：最近的时间点及各可见通道的原始值（不含显示偏移和滤波）。</summary>
    private string? HoverText((double[] Times, double[][] Raw) frame, double t, ChannelDisplayConfig? only)
    {
        var times = frame.Times;
        if (t < times[0] || t > times[^1]) return null;
        int i = Array.BinarySearch(times, t);
        if (i < 0)
        {
            i = ~i;
            if (i >= times.Length || (i > 0 && t - times[i - 1] < times[i] - t)) i--;
        }
        var sb = new StringBuilder($"{times[i]:0.000} s（原始值）");
        foreach (var cfg in only is null ? ChannelConfigs.Where(c => c.Visible) : [only])
        {
            if (cfg.ChannelIndex >= frame.Raw.Length || i >= frame.Raw[cfg.ChannelIndex].Length) continue;
            sb.Append('\n').Append(cfg.Name).Append("  ").Append(frame.Raw[cfg.ChannelIndex][i].ToString("G8", CultureInfo.CurrentCulture));
            if (!string.IsNullOrEmpty(cfg.Unit)) sb.Append(' ').Append(cfg.Unit);
        }
        return sb.ToString();
    }

    /// <summary>只替换叠加层并重绘，不重新取数。</summary>
    private void RefreshOverlays()
    {
        OverlayRefreshCount++;
        // 多图重建后旧图已不在界面上，先丢掉它们的记录。
        ForgetDetachedPlots();
        foreach (var (plot, (channel, items)) in _overlays.ToArray())
        {
            foreach (var item in items) plot.Remove(item);
            AddOverlays(plot, channel);
        }
        PlotControl?.Refresh();
        foreach (var control in MultiPlotControls) control.Refresh();
    }

    // ---- 区间分析操作 ----

    [RelayCommand]
    private void ApplyIntervalSelection()
    {
        if (!double.TryParse(IntervalStartInput, out double start) ||
            !double.TryParse(IntervalEndInput, out double end))
        {
            return;
        }

        var interval = new IntervalSelection(start, end);
        if (!interval.IsValid) return;

        CurrentInterval = interval;
        ComputeIntervalStatistics();
    }

    [RelayCommand]
    private void ClearIntervalSelection()
    {
        CurrentInterval = null;
        IntervalStatistics = null;
        IntervalStartInput = "";
        IntervalEndInput = "";
        IntervalStatisticsText = "";
    }

    [RelayCommand]
    private async Task ExportIntervalAsync()
    {
        if (CurrentInterval == null || IntervalStatistics == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出区间数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"interval_{CurrentInterval.StartTime:F1}s_{CurrentInterval.EndTime:F1}s.csv",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true) return;

        await ExportIntervalFromBuffersAsync(dialog.FileName);
    }

    private void ComputeIntervalStatistics()
    {
        if (CurrentInterval == null) return;

        double[] times;
        double[][] channels;
        string[] names;

        lock (_dataLock)
        {
            times = _timeBuffer.ToArray();
            channels = new double[_channelCount][];
            for (int i = 0; i < _channelCount; i++)
                channels[i] = _rawChannelBuffers[i].ToArray();
            names = _channelNames ?? Array.Empty<string>();
        }

        IntervalStatistics = IntervalStatisticsResult.Compute(
            CurrentInterval, times, channels, names);

        if (IntervalStatistics == null || IntervalStatistics.SampleCount == 0)
        {
            IntervalStatisticsText = "区间内无数据";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"区间: {CurrentInterval.StartTime:F2}s - {CurrentInterval.EndTime:F2}s | 采样点: {IntervalStatistics.SampleCount} | 时长: {CurrentInterval.Duration:F2}s");
        foreach (var stat in IntervalStatistics.ChannelStats)
        {
            sb.AppendLine($"  {stat.ChannelName}: 均值={stat.Mean:F3} 标准差={stat.StdDev:F3} 最小={stat.Min:F3} 最大={stat.Max:F3} 峰峰值={stat.PeakToPeak:F3}");
        }
        IntervalStatisticsText = sb.ToString().TrimEnd();
    }

    private async Task ExportIntervalFromBuffersAsync(string filePath)
    {
        if (CurrentInterval == null) return;

        double[] times;
        double[][] channels;
        string[] names;
        string[] units;

        lock (_dataLock)
        {
            times = _timeBuffer.ToArray();
            channels = new double[_channelCount][];
            for (int i = 0; i < _channelCount; i++)
                channels[i] = _rawChannelBuffers[i].ToArray();
            names = _channelNames?.ToArray() ?? Array.Empty<string>();
            units = _channelUnits.ToArray();
        }

        var (startIdx, count) = CurrentInterval.GetIndices(times);
        if (count == 0) return;

        await Task.Run(() =>
        {
            using var writer = new System.IO.StreamWriter(filePath, false, new System.Text.UTF8Encoding(true));
            // Header
            writer.Write("ElapsedSeconds");
            for (int ch = 0; ch < names.Length; ch++)
                writer.Write(",\"" + (names[ch] + " (" + units.ElementAtOrDefault(ch) + ")").Replace("\"", "\"\"") + "\"");
            writer.WriteLine();

            // Data
            for (int i = startIdx; i < startIdx + count; i++)
            {
                writer.Write(times[i].ToString("R", CultureInfo.InvariantCulture));
                for (int ch = 0; ch < channels.Length; ch++)
                    writer.Write("," + channels[ch][i].ToString("R", CultureInfo.InvariantCulture));
                writer.WriteLine();
            }
        });
    }

    // ---- 拖拽排序 ----

    public void ReorderChannels(int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex >= ChannelConfigs.Count || targetIndex >= ChannelConfigs.Count)
            return;

        var item = ChannelConfigs[sourceIndex];
        ChannelConfigs.RemoveAt(sourceIndex);
        ChannelConfigs.Insert(targetIndex, item);

        SaveChartOrderAsync().ConfigureAwait(false);
    }

    private async Task SaveChartOrderAsync()
    {
        if (_preferencesService == null) return;

        var order = ChannelConfigs.Select(c => c.ChannelIndex).ToArray();
        await _preferencesService.SetPreferenceAsync("ChartOrder", order);
    }

    public async Task LoadChartOrderAsync()
    {
        if (_preferencesService == null) return;

        var order = await _preferencesService.GetPreferenceAsync<int[]>("ChartOrder");
        if (order == null || order.Length != ChannelConfigs.Count) return;

        var reordered = new List<ChannelDisplayConfig>();
        foreach (var index in order)
        {
            var config = ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == index);
            if (config != null) reordered.Add(config);
        }

        if (reordered.Count == ChannelConfigs.Count)
        {
            ChannelConfigs.Clear();
            foreach (var config in reordered)
                ChannelConfigs.Add(config);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer.Stop();
        _dataBus.ProcessedReadingReceived -= OnReadingReceived;
        _dataBus.AcquisitionStarted -= OnAcquisitionStarted;
        _dataBus.AcquisitionStopped -= OnAcquisitionStopped;
        ChannelConfigs.CollectionChanged -= OnOffsetSourcesChanged;
        ComputedChannels.CollectionChanged -= OnOffsetSourcesChanged;
        foreach (var source in _offsetSources) source.PropertyChanged -= OnOffsetSourcePropertyChanged;
        _offsetSources.Clear();
    }
}

/// <summary>统计表的一行：原始值统计 + 单位 + 参与点数。</summary>
public sealed record LiveStatisticsRow(StatisticsResultItem Stats, string Unit, int Count)
{
    public string Name => Stats.ChannelName;
}
