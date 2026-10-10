using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.Services;
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

    // 时间轴 + 每通道显示值与原始值的环形缓冲，以及通道布局
    private readonly ChartSampleBuffer _samples = new();

    private readonly Dictionary<string, ScottPlot.IYAxis> _unitAxes = new();
    private int _layoutRefreshPending;
    private bool _isAcquiring;
    private bool _disposed;
    private ChartSampleSnapshot? _pausedData;

    /// <summary>当前通道布局（通道数、名称、单位）；读数的通道多于协议时会扩展。</summary>
    private ChartChannelLayout Layout => _samples.Layout;

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
    private long _dataPointCount;

    partial void OnDataPointCountChanged(long value) => Offsets.SetDataPointCount(value);

    // 每通道显示配置（偏移、颜色、可见性）
    [ObservableProperty]
    private ObservableCollection<ChannelDisplayConfig> _channelConfigs = new();

    // 自定义计算通道
    [ObservableProperty]
    private ObservableCollection<ComputedChannelDefinition> _computedChannels = new();

    // 缓存已编译的公式求值器
    private readonly ComputedChannelEvaluator _computedEvaluator = new();

    // 统计配置
    [ObservableProperty]
    private StatisticsConfig _statisticsConfig = new();

    /// <summary>降采样目标点数（0 = 禁用降采样）</summary>
    [ObservableProperty]
    private int _downsampleTargetCount = 2000;

    // ---- 区间分析 ----

    /// <summary>“区间”页签：选一段计算原始读数的统计量，并可导出这一段。</summary>
    public IntervalAnalysisViewModel Interval { get; }

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

    // 显示流水线：偏移 → 滤波 → 降采样
    private readonly DisplaySeriesPipeline _displayPipeline = new();

    private DisplayFilterSettings DisplayFilter => new(IsFilterEnabled, SelectedFilterType, FilterWindowSize);

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

    /// <summary>总场与梯度向导，确认后添加到 <see cref="ComputedChannels"/>。</summary>
    public ComputedChannelWizardViewModel Wizard { get; }

    // ---- 显示偏移 ----

    /// <summary>一键归零、取消归零和单个通道的显示偏移。</summary>
    public DisplayOffsetViewModel Offsets { get; }

    public RealtimeChartViewModel(DataBus dataBus, IUserPreferencesService? preferencesService = null, IDialogService? dialogs = null)
    {
        _dataBus = dataBus;
        _preferencesService = preferencesService;
        Wizard = new ComputedChannelWizardViewModel(ComputedChannels, ProtocolChannelSources);
        Interval = new IntervalAnalysisViewModel(_samples.SnapshotRaw, dialogs ?? new WpfDialogService());
        Interval.PropertyChanged += OnIntervalPropertyChanged;
        Offsets = new DisplayOffsetViewModel(ChannelConfigs, ComputedChannels, _samples, _computedEvaluator, _displayPipeline,
            CurrentDisplayedSamples, autoScaleY => { if (autoScaleY) AutoScaleY = true; RefreshPlot(); });

        _dataBus.ProcessedReadingReceived += OnReadingReceived;
        _dataBus.AcquisitionStarted += OnAcquisitionStarted;
        _dataBus.AcquisitionStopped += OnAcquisitionStopped;

        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / _refreshRate),
        };
        _renderTimer.Tick += OnRenderTick;
    }

    partial void OnRefreshRateChanged(int value)
    {
        if (value > 0)
            _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / value);
    }

    /// <summary>停止定时刷新，由调用方用 <see cref="RefreshPlot"/> 显式驱动（测试用，避免与定时器竞争）。</summary>
    internal void StopRenderTimer() => _renderTimer.Stop();

    private void OnAcquisitionStarted(SensorConfig config)
    {
        bool unitsChanged = !Layout.Units.SequenceEqual(config.ChannelUnits);
        _isAcquiring = true;
        _samples.Reset(config.ChannelCount, config.ChannelNames, config.ChannelUnits);

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DataPointCount = 0;
            IsPaused = false;

            // 初始化通道显示配置：仅当通道数量或名称发生变化时才重建，否则保留现有 Visible 等用户配置
            var layout = Layout;
            bool channelLayoutChanged = unitsChanged ||
                ChannelConfigs.Count != layout.Count ||
                !Enumerable.Range(0, layout.Count).All(i =>
                    i < ChannelConfigs.Count &&
                    i < layout.Names.Length &&
                    ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == i) is { } cfg &&
                    cfg.Name == layout.Names[i] && cfg.Unit == layout.Units.ElementAtOrDefault(i));

            if (channelLayoutChanged)
            {
                ChannelConfigs.Clear();
                var defaults = ChannelDisplayConfig.CreateDefaults(layout.Count, layout.Names);
                foreach (var cfg in defaults)
                {
                    cfg.Unit = layout.Units.ElementAtOrDefault(cfg.ChannelIndex) ?? "";
                    ChannelConfigs.Add(cfg);
                }

                // 通道布局改变时才清空计算通道（通道引用可能无效）
                ComputedChannels.Clear();
                _computedEvaluator.Clear();
            }
            else
            {
                // 通道布局未变，仅清空公式缓存以便下一 session 重新求值
                _computedEvaluator.Clear();
            }

            // 关闭向导面板
            Wizard.Close();

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

        // 读数的通道多于当前布局时（读数先于采集开始事件到达，或实际通道数多于配置），
        // 缓冲已补齐并扩展布局，这里在界面线程补上缺少的通道配置。
        if (_samples.Append(reading) && Interlocked.Exchange(ref _layoutRefreshPending, 1) == 0)
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var layout = Layout;
                foreach (var cfg in ChannelDisplayConfig.CreateDefaults(layout.Count, layout.Names))
                    if (!ChannelConfigs.Any(c => c.ChannelIndex == cfg.ChannelIndex))
                    { cfg.Unit = layout.Units[cfg.ChannelIndex]; ChannelConfigs.Add(cfg); }
                Interlocked.Exchange(ref _layoutRefreshPending, 0);
            });
    }

    public void RefreshPlot() => OnRenderTick(null, EventArgs.Empty);

    private ChartSampleSnapshot CapturePlotData(bool includeAll = false)
    {
        // Statistics with a zero window follows the plot. An unlimited plot needs all retained data.
        double window = includeAll || TimeWindowSeconds <= 0 ? 0
            : StatisticsConfig.WindowSeconds > 0 ? Math.Max(TimeWindowSeconds, StatisticsConfig.WindowSeconds) : TimeWindowSeconds;
        return _samples.Capture(window);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (IsPaused && sender is not null) return;
        var (times, channelData, rawData, totalCount) = _pausedData ?? CapturePlotData();
        int channelCount = Layout.Count;
        if (channelData.Length < channelCount)
        {
            channelData = Enumerable.Range(0, channelCount).Select(i => i < channelData.Length
                ? channelData[i] : Enumerable.Repeat(double.NaN, times.Length).ToArray()).ToArray();
            rawData = Enumerable.Range(0, channelCount).Select(i => i < rawData.Length
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
        int channelCount = Layout.Count;
        for (int ch = 0; ch < channelCount; ch++)
        {
            var config = ChannelConfigs.FirstOrDefault(c => c.ChannelIndex == ch);
            if (config != null && !config.Visible)
                continue;

            if (ch >= channelData.Length || channelData[ch].Length < startIdx + count)
                continue;

            var series = _displayPipeline.Prepare(windowTimes, channelData[ch].AsSpan(startIdx, count).ToArray(),
                config?.DisplayOffset ?? 0, DisplayFilter, DownsampleTargetCount);
            var sig = plot.Add.ScatterLine(series.Xs, series.Ys);
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
        int channelCount = Layout.Count;

        // 修复拖拽错位：按 ChannelConfigs 顺序迭代，用 config.ChannelIndex 取物理通道数据
        foreach (var config in ChannelConfigs)
        {
            if (!config.Visible) continue;

            int ch = config.ChannelIndex;
            if (ch >= channelCount || plotIdx >= MultiPlotControls.Count) break;

            var plotCtrl = MultiPlotControls[plotIdx];
            var plot = plotCtrl.Plot;
            plot.Clear();

            if (ch < channelData.Length && channelData[ch].Length >= startIdx + count)
            {
                var series = _displayPipeline.Prepare(windowTimes, channelData[ch].AsSpan(startIdx, count).ToArray(),
                    config.DisplayOffset, DisplayFilter, DownsampleTargetCount);
                var sig = plot.Add.ScatterLine(series.Xs, series.Ys);
                var (a, r, g, b) = config.ParseColor();
                sig.Color = new ScottPlot.Color(r, g, b, a);
                sig.LineWidth = 1.5f;

                // 图上统计标注（右上角）
                var stat = StatisticsResultItem.Compute(config.Name, series.Values);
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

            // 某通道缺这一点时按 0 求值
            var computedValues = _computedEvaluator.Evaluate(computed.Formula, channelData, startIdx, count, missing: 0);
            if (computedValues == null) continue;

            var plotCtrl = MultiPlotControls[plotIdx];
            var plot = plotCtrl.Plot;
            plot.Clear();

            var series = _displayPipeline.Prepare(windowTimes, computedValues, computed.DisplayOffset,
                DisplayFilter, DownsampleTargetCount);
            var compSig = plot.Add.ScatterLine(series.Xs, series.Ys);
            var (ca, cr, cg, cb) = new ChannelDisplayConfig { ColorHex = computed.ColorHex }.ParseColor();
            compSig.Color = new ScottPlot.Color(cr, cg, cb, ca);
            compSig.LineWidth = computed.LineWidth;

            // 计算通道统计标注
            var compStat = StatisticsResultItem.Compute(computed.Name, series.Values);
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

            // 某通道缺这一点时按 0 求值
            var computedValues = _computedEvaluator.Evaluate(computed.Formula, channelData, startIdx, count, missing: 0);
            if (computedValues == null) continue;

            var series = _displayPipeline.Prepare(windowTimes, computedValues, computed.DisplayOffset,
                DisplayFilter, DownsampleTargetCount);
            var compSig = plot.Add.ScatterLine(series.Xs, series.Ys);
            var (ca, cr, cg, cb) = new ChannelDisplayConfig { ColorHex = computed.ColorHex }.ParseColor();
            compSig.Color = new ScottPlot.Color(cr, cg, cb, ca);
            compSig.LineWidth = computed.LineWidth;
            compSig.LegendText = computed.Name;
            if (_unitAxes.TryGetValue(computed.Unit, out var axis)) compSig.Axes.YAxis = axis;
        }
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
        var layout = Layout;
        if (count <= 0 || layout.Count <= 0) { StatisticsText = ""; StatisticsRows = []; return; }

        var statConfig = StatisticsConfig;
        var rows = LiveStatisticsCalculator
            .Compute(times, channelData, layout.Count, layout.Names, startIdx, count, statConfig.WindowSeconds)
            .Select(s => new LiveStatisticsRow(s.Stats, layout.Units.ElementAtOrDefault(s.ChannelIndex) ?? "", s.Count))
            .ToList();
        var now = DateTime.UtcNow;
        if ((now - _lastStatisticsRowsUpdate).TotalMilliseconds >= 500 || rows.Count != StatisticsRows.Count)
        {
            _lastStatisticsRowsUpdate = now;
            StatisticsRows = rows;
        }
        StatisticsText = "原始数据  ·  " + string.Join("  |  ", rows.Select(r => r.Stats.Format(statConfig)));
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
        _samples.Clear();
        DataPointCount = 0;
        StatisticsText = "暂无数据";
        StatisticsRows = [];
        Interval.ClearIntervalSelection();
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
        Wizard.Close();
    }

    [RelayCommand]
    private void RemoveComputedChannel(ComputedChannelDefinition? def)
    {
        if (def != null)
        {
            ComputedChannels.Remove(def);
            _computedEvaluator.Forget(def.Formula);
        }
    }

    /// <summary>向导可选的原始通道：协议通道按索引，公式变量为 CH0、CH1…</summary>
    private IReadOnlyList<SourceOption> ProtocolChannelSources()
    {
        var layout = Layout;
        var sources = new List<SourceOption>();
        for (int i = 0; i < layout.Count; i++)
        {
            var label = i < layout.Names.Length ? layout.Names[i] : $"CH{i}";
            sources.Add(new SourceOption { Label = label, FormulaExpr = $"CH{i}", Unit = layout.Units.ElementAtOrDefault(i) ?? "" });
        }
        return sources;
    }

    /// <summary>一键归零用的数据：当前画面（暂停时为冻结的数据）的显示值、时间窗口的起点与点数，以及显示滤波设置。</summary>
    private DisplayedSamples CurrentDisplayedSamples()
    {
        var (times, display, _, _) = _pausedData ?? CapturePlotData();
        int start = WindowStartIndex(times);
        return new DisplayedSamples(display, start, times.Length - start, DisplayFilter);
    }

    // ---- 通道颜色与顺序 ----

    /// <summary>通道颜色可选色块：与默认色相同的 8 色。</summary>
    public static IReadOnlyList<string> ChannelSwatches { get; } = ChannelDisplayConfig.PresetColors;

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
        Interval.IntervalStartInput = from.ToString("0.###", CultureInfo.CurrentCulture);
        Interval.IntervalEndInput = to.ToString("0.###", CultureInfo.CurrentCulture);
        Interval.ApplyIntervalSelection();
        RefreshOverlays();
        return Interval.CurrentInterval != null;
    }

    /// <summary>区间选定或清除时重绘阴影。</summary>
    private void OnIntervalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntervalAnalysisViewModel.CurrentInterval)) RefreshOverlays();
    }

    /// <summary>在一张图上加区间阴影、拖动预览和十字准线读数。channel 为多图模式下这张图对应的通道。</summary>
    private void AddOverlays(ScottPlot.Plot plot, ChannelDisplayConfig? channel)
    {
        var items = new List<ScottPlot.IPlottable>();
        if (Interval.CurrentInterval is { } interval)
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
            var text = CrosshairReadout.Format(frame.Times, frame.Raw, t,
                channel is null ? ChannelConfigs.Where(c => c.Visible) : [channel]);
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
        Interval.PropertyChanged -= OnIntervalPropertyChanged;
        Offsets.Dispose();
    }
}

/// <summary>统计表的一行：原始值统计 + 单位 + 参与点数。</summary>
public sealed record LiveStatisticsRow(StatisticsResultItem Stats, string Unit, int Count)
{
    public string Name => Stats.ChannelName;
}
