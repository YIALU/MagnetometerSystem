using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Core.Services;
using ScottPlot;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 实时曲线绘制 ViewModel
/// </summary>
public partial class RealtimeChartViewModel : ObservableObject, IDisposable
{
    private readonly DataBus _dataBus;
    private readonly DispatcherTimer _renderTimer;

    // 每个通道的数据缓冲（时间戳 + 值）
    private readonly CircularBuffer<double> _timeBuffer = new(100000);
    private CircularBuffer<double>[] _channelBuffers;
    private readonly object _dataLock = new();
    private const int MaxChannels = 16;

    private int _channelCount;
    private string[] _channelNames = ["CH0"];
    private DateTime _startTime;
    private bool _isAcquiring;
    private bool _disposed;

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

    [ObservableProperty]
    private string _statisticsText = "";

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

    public RealtimeChartViewModel(DataBus dataBus)
    {
        _dataBus = dataBus;

        // 预创建通道缓冲
        _channelBuffers = new CircularBuffer<double>[MaxChannels];
        for (int i = 0; i < MaxChannels; i++)
            _channelBuffers[i] = new CircularBuffer<double>(100000);

        _dataBus.ReadingReceived += OnReadingReceived;
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

    private void OnAcquisitionStarted(SensorConfig config)
    {
        int channelCount = Math.Min(config.ChannelCount, MaxChannels);
        string[] channelNames = config.ChannelNames;
        _startTime = DateTime.Now;
        _isAcquiring = true;

        lock (_dataLock)
        {
            _channelCount = channelCount;
            _channelNames = channelNames;
            _timeBuffer.Clear();
            for (int i = 0; i < _channelBuffers.Length; i++)
                _channelBuffers[i].Clear();
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DataPointCount = 0;
            IsPaused = false;

            // 初始化通道显示配置
            ChannelConfigs.Clear();
            var defaults = ChannelDisplayConfig.CreateDefaults(_channelCount, _channelNames);
            foreach (var cfg in defaults)
                ChannelConfigs.Add(cfg);

            // 清空计算通道
            ComputedChannels.Clear();
            _formulaCache.Clear();

            // 关闭向导面板
            IsAddingTotalField = false;
            IsAddingGradient = false;

            SetupPlot();
            _renderTimer.Start();
        });
    }

    private void OnAcquisitionStopped()
    {
        _isAcquiring = false;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _renderTimer.Stop();
        });
    }

    private void OnReadingReceived(MagnetometerReading reading)
    {
        if (IsPaused) return;

        var elapsed = (reading.Timestamp - _startTime).TotalSeconds;

        lock (_dataLock)
        {
            _timeBuffer.Add(elapsed);
            for (int i = 0; i < Math.Min(reading.ChannelValues.Length, _channelBuffers.Length); i++)
            {
                _channelBuffers[i].Add(reading.ChannelValues[i]);
            }
        }

        Interlocked.Increment(ref _dataPointCount);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        double[] times;
        double[][] channelData;

        lock (_dataLock)
        {
            times = _timeBuffer.ToArray();
            channelData = new double[_channelCount][];
            for (int i = 0; i < _channelCount; i++)
                channelData[i] = _channelBuffers[i].ToArray();
        }

        if (times.Length == 0)
        {
            if (!IsMultiPlotMode && PlotControl != null) PlotControl.Refresh();
            return;
        }

        DataPointCount = times.Length;

        double xMax = times[^1];
        double xMin = TimeWindowSeconds > 0 ? xMax - TimeWindowSeconds : times[0];

        int startIdx = 0;
        if (TimeWindowSeconds > 0)
        {
            for (int i = times.Length - 1; i >= 0; i--)
            {
                if (times[i] < xMin) { startIdx = i + 1; break; }
            }
        }
        if (startIdx >= times.Length) startIdx = times.Length - 1;
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

        UpdateStatistics(times, channelData, startIdx, count);
    }

    private void RenderSinglePlot(double[] windowTimes, double[][] channelData,
        int startIdx, int count, double xMin, double xMax)
    {
        if (PlotControl == null) return;

        var plot = PlotControl.Plot;
        plot.Clear();

        // 绘制各通道
        for (int ch = 0; ch < _channelCount; ch++)
        {
            var config = ch < ChannelConfigs.Count ? ChannelConfigs[ch] : null;
            if (config != null && !config.Visible)
                continue;

            if (channelData[ch].Length < startIdx + count)
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

            if (config != null)
            {
                var (a, r, g, b) = config.ParseColor();
                sig.Color = new ScottPlot.Color(r, g, b, a);
            }

            sig.LineWidth = 1.5f;
            sig.LegendText = config?.Name ?? (ch < _channelNames.Length ? _channelNames[ch] : $"CH{ch}");
        }

        // 绘制计算通道
        RenderComputedChannels(plot, windowTimes, channelData, startIdx, count);

        ConfigurePlotAxes(plot, xMin, xMax);
        PlotControl.Refresh();
    }

    private void RenderMultiPlot(double[] windowTimes, double[][] channelData,
        int startIdx, int count, double xMin, double xMax)
    {
        int plotIdx = 0;
        for (int ch = 0; ch < _channelCount; ch++)
        {
            var config = ch < ChannelConfigs.Count ? ChannelConfigs[ch] : null;
            if (config != null && !config.Visible)
                continue;

            if (plotIdx >= MultiPlotControls.Count) break;
            var plotCtrl = MultiPlotControls[plotIdx];
            var plot = plotCtrl.Plot;
            plot.Clear();

            if (channelData[ch].Length >= startIdx + count)
            {
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
                if (config != null)
                {
                    var (a, r, g, b) = config.ParseColor();
                    sig.Color = new ScottPlot.Color(r, g, b, a);
                }
                sig.LineWidth = 1.5f;
            }

            string name = config?.Name ?? (ch < _channelNames.Length ? _channelNames[ch] : $"CH{ch}");
            plot.Axes.Left.Label.Text = name;
            ConfigurePlotAxes(plot, xMin, xMax);
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
                var chVals = new double[_channelCount];
                for (int ch = 0; ch < _channelCount; ch++)
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
        if (count <= 0 || _channelCount <= 0) { StatisticsText = ""; return; }

        // 确定统计窗口
        var statConfig = StatisticsConfig;
        int statStartIdx = startIdx;
        int statCount = count;

        if (statConfig.WindowSeconds > 0 && times.Length > 0)
        {
            double statXMin = times[startIdx + count - 1] - statConfig.WindowSeconds;
            for (int i = startIdx + count - 1; i >= startIdx; i--)
            {
                if (times[i] < statXMin) { statStartIdx = i + 1; break; }
            }
            if (statStartIdx > startIdx + count - 1) statStartIdx = startIdx + count - 1;
            statCount = startIdx + count - statStartIdx;
        }

        var lines = new List<string>();
        for (int ch = 0; ch < _channelCount; ch++)
        {
            if (ch >= channelData.Length || channelData[ch].Length < statStartIdx + statCount)
                continue;

            var span = channelData[ch].AsSpan(statStartIdx, statCount);
            string name = ch < _channelNames.Length ? _channelNames[ch] : $"CH{ch}";
            var result = StatisticsResultItem.Compute(name, span);
            lines.Add(result.Format(statConfig));
        }
        StatisticsText = string.Join("  |  ", lines);
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
        plot.Axes.Left.Label.Text = "磁场 (nT)";
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
    }

    [RelayCommand]
    private void ToggleChannel(int channelIndex)
    {
        if (channelIndex >= 0 && channelIndex < ChannelConfigs.Count)
        {
            ChannelConfigs[channelIndex].Visible = !ChannelConfigs[channelIndex].Visible;
        }
    }

    [RelayCommand]
    private void ClearChart()
    {
        lock (_dataLock)
        {
            _timeBuffer.Clear();
            for (int i = 0; i < _channelBuffers.Length; i++)
                _channelBuffers[i].Clear();
        }
        DataPointCount = 0;

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
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Calc{ComputedChannels.Count}",
            Formula = "CH0",
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
        if (WizardSourceA < 0 || WizardSourceA >= WizardRawSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardRawSources.Count
            || WizardSourceC < 0 || WizardSourceC >= WizardRawSources.Count)
        {
            IsAddingTotalField = false;
            return;
        }

        var a = WizardRawSources[WizardSourceA].FormulaExpr;
        var b = WizardRawSources[WizardSourceB].FormulaExpr;
        var c = WizardRawSources[WizardSourceC].FormulaExpr;
        var formula = $"sqrt({a}*{a} + {b}*{b} + {c}*{c})";

        int totalCount = ComputedChannels.Count(ch => ch.ChannelType == ComputedChannelType.TotalField) + 1;
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Total{totalCount}",
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
        BuildWizardGradientSources();
        WizardSourceA = 0;
        WizardSourceB = Math.Min(1, WizardGradientSources.Count - 1);
        IsAddingTotalField = false;
        IsAddingGradient = true;
    }

    [RelayCommand]
    private void ConfirmAddGradient()
    {
        if (WizardSourceA < 0 || WizardSourceA >= WizardGradientSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardGradientSources.Count)
        {
            IsAddingGradient = false;
            return;
        }

        var a = WizardGradientSources[WizardSourceA].FormulaExpr;
        var b = WizardGradientSources[WizardSourceB].FormulaExpr;
        var formula = GradientBaselineDistance != 0 && GradientBaselineDistance != 1.0
            ? $"(({a}) - ({b})) / {GradientBaselineDistance:R}"
            : $"({a}) - ({b})";

        int gradCount = ComputedChannels.Count(ch => ch.ChannelType == ComputedChannelType.Gradient) + 1;
        ComputedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Grad{gradCount}",
            Formula = formula,
            ChannelType = ComputedChannelType.Gradient,
            ColorHex = "#FF808080",
        });

        IsAddingGradient = false;
    }

    [RelayCommand]
    private void CancelAddWizard()
    {
        IsAddingTotalField = false;
        IsAddingGradient = false;
    }

    /// <summary>
    /// 构建向导可选的原始通道列表
    /// </summary>
    private void BuildWizardRawSources()
    {
        WizardRawSources.Clear();
        for (int i = 0; i < _channelCount; i++)
        {
            var label = i < _channelNames.Length ? _channelNames[i] : $"CH{i}";
            WizardRawSources.Add(new SourceOption { Label = label, FormulaExpr = $"CH{i}" });
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
            WizardGradientSources.Add(new SourceOption { Label = label, FormulaExpr = $"CH{i}" });
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
                });
            }
        }
    }

    // ---- 自动偏移 ----

    /// <summary>
    /// 自动偏移：计算指定通道的平均值，设置 DisplayOffset = -average
    /// </summary>
    [RelayCommand]
    private void AutoOffsetChannel(int channelIndex)
    {
        if (channelIndex < 0 || channelIndex >= ChannelConfigs.Count)
            return;

        double[] data;
        lock (_dataLock)
        {
            if (channelIndex >= _channelBuffers.Length)
                return;
            data = _channelBuffers[channelIndex].ToArray();
        }

        if (data.Length == 0) return;

        double avg = 0;
        for (int i = 0; i < data.Length; i++)
            avg += data[i];
        avg /= data.Length;

        ChannelConfigs[channelIndex].DisplayOffset = -avg;
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
            var chVals = new double[_channelCount];
            for (int ch = 0; ch < _channelCount; ch++)
            {
                if (channelData[ch].Length > i)
                    chVals[ch] = channelData[ch][i];
            }
            double val = evaluator.Evaluate(chVals);
            if (!double.IsNaN(val))
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
            channels = new double[_channelBuffers.Length][];
            for (int i = 0; i < _channelBuffers.Length; i++)
                channels[i] = _channelBuffers[i].ToArray();
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

        lock (_dataLock)
        {
            times = _timeBuffer.ToArray();
            channels = new double[_channelBuffers.Length][];
            for (int i = 0; i < _channelBuffers.Length; i++)
                channels[i] = _channelBuffers[i].ToArray();
            names = _channelNames ?? Array.Empty<string>();
        }

        var (startIdx, count) = CurrentInterval.GetIndices(times);
        if (count == 0) return;

        await Task.Run(() =>
        {
            using var writer = new System.IO.StreamWriter(filePath, false, new System.Text.UTF8Encoding(true));
            // Header
            writer.Write("ElapsedSeconds");
            for (int ch = 0; ch < names.Length; ch++)
                writer.Write($",{names[ch]}");
            writer.WriteLine();

            // Data
            for (int i = startIdx; i < startIdx + count; i++)
            {
                writer.Write(times[i].ToString("R"));
                for (int ch = 0; ch < channels.Length; ch++)
                    writer.Write($",{channels[ch][i]:R}");
                writer.WriteLine();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer.Stop();
        _dataBus.ReadingReceived -= OnReadingReceived;
        _dataBus.AcquisitionStarted -= OnAcquisitionStarted;
        _dataBus.AcquisitionStopped -= OnAcquisitionStopped;
    }
}
