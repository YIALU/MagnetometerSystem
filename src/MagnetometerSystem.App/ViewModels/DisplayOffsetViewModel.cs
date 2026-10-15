using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 一键归零用的数据：当前画面的显示值（按通道索引，暂停时为冻结的数据）、时间窗口在其中的起点与点数，以及显示滤波设置。
/// </summary>
public readonly record struct DisplayedSamples(double[][] Display, int Start, int Count, DisplayFilterSettings Filter);

/// <summary>
/// 采集页曲线的显示偏移：一键归零、取消归零，以及单个通道（含计算通道）按均值归零或清除偏移。
/// 只改通道配置上的显示偏移，不改变保存的原始值、统计表和十字准线读数。
/// </summary>
public partial class DisplayOffsetViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<ChannelDisplayConfig> _channels;
    private readonly ObservableCollection<ComputedChannelDefinition> _computedChannels;
    private readonly ChartSampleBuffer _samples;
    private readonly ComputedChannelEvaluator _evaluator;
    private readonly DisplaySeriesPipeline _pipeline;
    private readonly Func<DisplayedSamples> _displayed;
    private readonly Action<bool> _redraw;
    private readonly HashSet<INotifyPropertyChanged> _offsetSources = new();
    private bool _hasData;
    private bool _disposed;

    /// <param name="channels">图表的通道配置，偏移写在这里。</param>
    /// <param name="computedChannels">图表的计算通道。</param>
    /// <param name="samples">曲线缓冲；单个通道按均值归零时用整个缓冲的显示值。</param>
    /// <param name="evaluator">计算通道的公式求值，与绘图共用缓存。</param>
    /// <param name="pipeline">显示流水线；一键归零按滤波后的曲线计算。</param>
    /// <param name="displayed">取当前画面的数据（暂停时为冻结的数据）。</param>
    /// <param name="redraw">偏移改变后重绘曲线；参数为 true 时先把主纵轴改为自动。</param>
    public DisplayOffsetViewModel(
        ObservableCollection<ChannelDisplayConfig> channels,
        ObservableCollection<ComputedChannelDefinition> computedChannels,
        ChartSampleBuffer samples,
        ComputedChannelEvaluator evaluator,
        DisplaySeriesPipeline pipeline,
        Func<DisplayedSamples> displayed,
        Action<bool> redraw)
    {
        _channels = channels;
        _computedChannels = computedChannels;
        _samples = samples;
        _evaluator = evaluator;
        _pipeline = pipeline;
        _displayed = displayed;
        _redraw = redraw;
        _channels.CollectionChanged += OnOffsetSourcesChanged;
        _computedChannels.CollectionChanged += OnOffsetSourcesChanged;
        TrackOffsetSources();
    }

    /// <summary>有通道（含计算通道）设置了显示偏移时为 true，工具栏据此显示“取消归零”。</summary>
    public bool HasDisplayOffsets =>
        _channels.Any(c => c.DisplayOffset != 0) || _computedChannels.Any(c => c.DisplayOffset != 0);

    private void OnOffsetSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TrackOffsetSources();
        OnPropertyChanged(nameof(HasDisplayOffsets));
    }

    /// <summary>
    /// 按两个列表的当前内容对齐偏移变化的订阅。通道列表会整体清空重建（Reset 不带旧项），所以不按事件里的增删项处理。
    /// </summary>
    private void TrackOffsetSources()
    {
        var current = _channels.Cast<INotifyPropertyChanged>().Concat(_computedChannels).ToHashSet();
        foreach (var gone in _offsetSources.Where(source => !current.Contains(source)).ToList())
        {
            gone.PropertyChanged -= OnOffsetSourcePropertyChanged;
            _offsetSources.Remove(gone);
        }
        foreach (var source in current)
            if (_offsetSources.Add(source)) source.PropertyChanged += OnOffsetSourcePropertyChanged;
    }

    private void OnOffsetSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelDisplayConfig.DisplayOffset))
            OnPropertyChanged(nameof(HasDisplayOffsets));
    }

    private bool CanZeroVisibleChannels() => _hasData;

    /// <summary>图表点数变化时调用。绘图每次刷新都会更新点数，只在“有无数据”变化时通知按钮。</summary>
    internal void SetDataPointCount(long count)
    {
        if ((count > 0) == _hasData) return;
        _hasData = count > 0;
        ZeroVisibleChannelsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 一键归零：把当前显示的每条曲线（可见通道与启用的计算通道）在时间窗口内的均值移到 0。
    /// 只设置显示偏移（暂停时用冻结的数据），不改变保存的原始值、统计表和十字准线读数。
    /// 开启显示滤波时按滤波后的曲线计算。手动纵轴范围是按原始数值设的，归零后曲线会移出范围，因此主 Y 轴改为自动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanZeroVisibleChannels))]
    private void ZeroVisibleChannels()
    {
        var (display, start, count, filter) = _displayed();
        if (count == 0) return;

        foreach (var config in _channels.Where(c => c.Visible))
        {
            int ch = config.ChannelIndex;
            // 曲线按“加偏移 → 滤波”绘制；移动平均与中值滤波都与常数偏移可交换，
            // 所以用滤波后曲线的均值作偏移，画出来的（滤波后）曲线均值正好为 0。
            if (ch < display.Length && display[ch].Length >= start + count
                && FiniteMean(_pipeline.Filter(display[ch].AsSpan(start, count).ToArray(), filter)) is { } mean)
                config.DisplayOffset = -mean;
        }

        foreach (var computed in _computedChannels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Formula)))
        {
            // 某通道缺这一点时按 NaN 求值，不计入均值
            if (_evaluator.Evaluate(computed.Formula, display, start, count, missing: double.NaN) is not { } values)
                continue;
            if (FiniteMean(_pipeline.Filter(values, filter)) is { } mean) computed.DisplayOffset = -mean;
        }

        _redraw(true);
    }

    /// <summary>取消归零：清除所有通道与计算通道的显示偏移。</summary>
    [RelayCommand]
    private void ClearDisplayOffsets()
    {
        foreach (var config in _channels) config.DisplayOffset = 0;
        foreach (var computed in _computedChannels) computed.DisplayOffset = 0;
        _redraw(false);
    }

    [RelayCommand]
    private void ClearChannelOffset(int channelIndex)
    {
        if (_channels.FirstOrDefault(c => c.ChannelIndex == channelIndex) is { } config)
            config.DisplayOffset = 0;
    }

    /// <summary>
    /// 自动偏移：计算指定通道的平均值，设置 DisplayOffset = -average（整个缓冲的显示值、不滤波）
    /// </summary>
    [RelayCommand]
    private void AutoOffsetChannel(int channelIndex)
    {
        var config = _channels.FirstOrDefault(c => c.ChannelIndex == channelIndex);
        if (config is null) return;
        if (_samples.SnapshotDisplay(channelIndex) is not { } data) return;

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

        if (_evaluator.GetOrCreate(def.Formula) == null) return;

        var channelData = _samples.SnapshotDisplay();
        int sampleCount = channelData.Length > 0 ? channelData[0].Length : 0;
        if (sampleCount == 0) return;

        // 整个缓冲、不滤波；某通道缺这一点时按 0 求值
        var values = _evaluator.Evaluate(def.Formula, channelData, 0, sampleCount, missing: 0);
        if (values != null && FiniteMean(values) is { } mean)
            def.DisplayOffset = -mean;
    }

    private static double? FiniteMean(ReadOnlySpan<double> values)
    {
        double sum = 0;
        int n = 0;
        foreach (var v in values)
            if (double.IsFinite(v)) { sum += v; n++; }
        return n > 0 ? sum / n : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channels.CollectionChanged -= OnOffsetSourcesChanged;
        _computedChannels.CollectionChanged -= OnOffsetSourcesChanged;
        foreach (var source in _offsetSources) source.PropertyChanged -= OnOffsetSourcePropertyChanged;
        _offsetSources.Clear();
    }
}
