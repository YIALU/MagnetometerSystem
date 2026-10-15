using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Charting;

namespace MagnetometerSystem.App.Views;

public partial class RealtimeChartView : UserControl
{
    private RealtimeChartViewModel? _boundViewModel;
    // 曲线由视图持有的图画出：视图模型只给出每帧内容，绘图器在加载时接上视图模型、卸载时解除。
    private readonly ChartRenderer _renderer;
    private readonly HashSet<Core.Models.ChannelDisplayConfig> _subscribedChannels = new();
    private readonly HashSet<Core.Models.ComputedChannelDefinition> _subscribedComputed = new();
    public RealtimeChartView()
    {
        InitializeComponent();
        _renderer = new ChartRenderer(WpfPlot1);
        AttachInteraction(WpfPlot1);
        Unloaded += OnUnloaded;
    }

    /// <summary>这个视图的绘图器（测试用）。</summary>
    internal ChartRenderer Renderer => _renderer;

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RealtimeChartViewModel vm)
        {
            if (ReferenceEquals(_boundViewModel, vm)) return;
            DetachViewModel();
            _boundViewModel = vm;
            _renderer.Attach(vm);
            ChartFontHelper.Apply(WpfPlot1.Plot);
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ChannelConfigs.CollectionChanged += OnChannelConfigsChanged;
            vm.ComputedChannels.CollectionChanged += OnComputedChannelsChanged;
            vm.WorkspaceLayout.PropertyChanged += OnWorkspaceLayoutChanged;
            vm.StatisticsConfig.PropertyChanged += OnStatisticsChanged;

            // 订阅已存在的通道配置的属性变化
            SyncItemSubscriptions();

            // 恢复多图表视图（如果之前是多图表模式）
            if (vm.IsMultiPlotMode)
            {
                RebuildMultiPlotControls();
            }
            vm.RefreshPlot();
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => DetachViewModel();

    private void DetachViewModel()
    {
        if (_boundViewModel is not { } vm) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.ChannelConfigs.CollectionChanged -= OnChannelConfigsChanged;
        vm.ComputedChannels.CollectionChanged -= OnComputedChannelsChanged;
        vm.WorkspaceLayout.PropertyChanged -= OnWorkspaceLayoutChanged;
        vm.StatisticsConfig.PropertyChanged -= OnStatisticsChanged;
        foreach (var cfg in _subscribedChannels) cfg.PropertyChanged -= OnChannelConfigPropertyChanged;
        foreach (var cfg in _subscribedComputed) cfg.PropertyChanged -= OnComputedPropertyChanged;
        _subscribedChannels.Clear();
        _subscribedComputed.Clear();
        // 卸载后这些图不再显示：绘图器解除订阅并丢掉多图和叠加层记录，旧图及其数据不再被视图模型留住。
        _renderer.Detach();
        _boundViewModel = null;
    }

    private void SyncItemSubscriptions()
    {
        if (_boundViewModel is not { } vm) return;
        foreach (var cfg in _subscribedChannels) cfg.PropertyChanged -= OnChannelConfigPropertyChanged;
        foreach (var cfg in _subscribedComputed) cfg.PropertyChanged -= OnComputedPropertyChanged;
        _subscribedChannels.Clear(); _subscribedComputed.Clear();
        foreach (var cfg in vm.ChannelConfigs) { cfg.PropertyChanged += OnChannelConfigPropertyChanged; _subscribedChannels.Add(cfg); }
        foreach (var cfg in vm.ComputedChannels) { cfg.PropertyChanged += OnComputedPropertyChanged; _subscribedComputed.Add(cfg); }
    }

    private void OnComputedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Core.Models.ComputedChannelDefinition.Enabled)) RebuildMultiPlotControls();
        _boundViewModel?.RefreshPlot();
    }

    private void OnWorkspaceLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceLayoutViewModel.IsFocused)) RebuildMultiPlotControls();
    }

    private void OnStatisticsChanged(object? sender, PropertyChangedEventArgs e) => _boundViewModel?.RefreshPlot();

    private void OnPlotAreaSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        // 多图行高随绘图区填满；高度只在明显变化时重建，避免拖动分隔条时反复重建控件。
        if (_boundViewModel is { IsMultiPlotMode: true } && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 4)
            RebuildMultiPlotControls();
    }

    private void OnChannelConfigsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SyncItemSubscriptions();

        if (DataContext is RealtimeChartViewModel vm && vm.IsMultiPlotMode)
            RebuildMultiPlotControls();
    }

    private void OnChannelConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Core.Models.ChannelDisplayConfig.Visible))
            RebuildMultiPlotControls();
        if (e.PropertyName != nameof(Core.Models.ChannelDisplayConfig.LatestValue)) _boundViewModel?.RefreshPlot();
    }

    private void OnComputedChannelsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SyncItemSubscriptions();
        if (DataContext is RealtimeChartViewModel vm && vm.IsMultiPlotMode)
        {
            RebuildMultiPlotControls();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RealtimeChartViewModel.IsMultiPlotMode) or
            nameof(RealtimeChartViewModel.MultiPlotColumnCount) or
            nameof(RealtimeChartViewModel.MultiPlotHeight) or nameof(RealtimeChartViewModel.IsChartHeightAutomatic))
        {
            RebuildMultiPlotControls();
        }
        if (e.PropertyName is nameof(RealtimeChartViewModel.AutoScaleY) or nameof(RealtimeChartViewModel.YMin) or nameof(RealtimeChartViewModel.YMax) or nameof(RealtimeChartViewModel.TimeWindowSeconds) or nameof(RealtimeChartViewModel.IsPaused)
            or nameof(RealtimeChartViewModel.ShowGrid) or nameof(RealtimeChartViewModel.AutoScroll)
            or nameof(RealtimeChartViewModel.IsFilterEnabled) or nameof(RealtimeChartViewModel.FilterWindowSize)
            or nameof(RealtimeChartViewModel.SelectedFilterType) or nameof(RealtimeChartViewModel.DownsampleTargetCount))
            _boundViewModel?.RefreshPlot();
    }

    private void RebuildMultiPlotControls()
    {
        if (DataContext is not RealtimeChartViewModel vm) return;

        MultiPlotPanel.Children.Clear();
        _renderer.SetMultiPlots([]);

        if (!vm.IsMultiPlotMode) { vm.RefreshPlot(); return; }

        // 统计可见通道数和启用的计算通道数
        int visibleChannelCount = vm.ChannelConfigs.Count(c => c.Visible);
        int enabledComputedCount = vm.ComputedChannels.Count(c => c.Enabled);
        int totalPlotCount = visibleChannelCount + enabledComputedCount;

        if (totalPlotCount == 0) return;

        // 统一布局：完全由 MultiPlotColumnCount 决定列数，不对通道数特判
        int columnCount = Math.Max(1, vm.MultiPlotColumnCount);
        int rowCount = (int)Math.Ceiling((double)totalPlotCount / columnCount);
        double plotHeight = vm.MultiPlotHeight;
        if (PlotArea.ActualHeight > 0)
            plotHeight = Math.Max(150, (PlotArea.ActualHeight - 4) / rowCount);

        // 创建网格布局
        var grid = new System.Windows.Controls.Grid();

        // 定义列
        for (int i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });
        }

        // 定义行
        for (int i = 0; i < rowCount; i++)
        {
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = new System.Windows.GridLength(plotHeight)
            });
        }

        var plots = new List<ScottPlot.WPF.WpfPlot>();
        int channelIndex = 0;
        foreach (var config in vm.ChannelConfigs)
        {
            if (!config.Visible) continue;

            int row = channelIndex / columnCount;
            int col = channelIndex % columnCount;

            var wpfPlot = new ScottPlot.WPF.WpfPlot
            {
                Margin = new System.Windows.Thickness(2),
            };
            wpfPlot.MouseWheel += OnPlotMouseWheel;
            AttachInteraction(wpfPlot);
            ChartFontHelper.Apply(wpfPlot.Plot);

            System.Windows.Controls.Grid.SetRow(wpfPlot, row);
            System.Windows.Controls.Grid.SetColumn(wpfPlot, col);

            grid.Children.Add(wpfPlot);
            plots.Add(wpfPlot);

            channelIndex++;
        }

        // 为计算通道创建图表（与 raw 通道使用同一套行列公式，避免覆盖）
        foreach (var computed in vm.ComputedChannels)
        {
            if (!computed.Enabled) continue;

            int row = channelIndex / columnCount;
            int col = channelIndex % columnCount;

            var wpfPlot = new ScottPlot.WPF.WpfPlot
            {
                Margin = new System.Windows.Thickness(2),
            };
            wpfPlot.MouseWheel += OnPlotMouseWheel;
            AttachInteraction(wpfPlot);
            ChartFontHelper.Apply(wpfPlot.Plot);

            System.Windows.Controls.Grid.SetRow(wpfPlot, row);
            System.Windows.Controls.Grid.SetColumn(wpfPlot, col);

            grid.Children.Add(wpfPlot);
            plots.Add(wpfPlot);

            channelIndex++;
        }

        _renderer.SetMultiPlots(plots);
        MultiPlotPanel.Children.Add(grid);
        vm.RefreshPlot();
    }

    // ---- 曲线交互：左键拖动选区间，悬停显示十字准线 ----
    // 时间轴每次刷新都由时间窗口决定，ScottPlot 默认的左键平移没有持久效果，这里改作区间选择。

    private System.Windows.Point? _dragOrigin;

    private void AttachInteraction(ScottPlot.WPF.WpfPlot plot)
    {
        plot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        plot.PreviewMouseMove += OnPlotMouseMove;
        plot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
        plot.MouseLeave += OnPlotMouseLeave;
    }

    private static double PlotSeconds(ScottPlot.WPF.WpfPlot plot, MouseEventArgs e) =>
        plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e)).X;

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot || DataContext is not RealtimeChartViewModel { DataPointCount: > 0 } vm) return;
        _dragOrigin = e.GetPosition(plot);
        vm.BeginPlotSelection(PlotSeconds(plot, e));
        plot.CaptureMouse();
        e.Handled = true;
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot || DataContext is not RealtimeChartViewModel vm) return;
        var seconds = PlotSeconds(plot, e);
        if (_dragOrigin is not null) vm.UpdatePlotSelection(seconds);
        vm.SetHoverTime(seconds);
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot || _dragOrigin is not { } origin || DataContext is not RealtimeChartViewModel vm) return;
        _dragOrigin = null;
        plot.ReleaseMouseCapture();
        e.Handled = true;
        if (Math.Abs(e.GetPosition(plot).X - origin.X) < 4) { vm.CancelPlotSelection(); return; }
        if (!vm.EndPlotSelection(PlotSeconds(plot, e))) return;
        // 在采集页：打开右侧“区间”页显示统计。
        for (System.Windows.DependencyObject? p = this; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is AcquisitionWorkspaceView)
            {
                vm.WorkspaceLayout.SideTab = WorkspaceLayoutViewModel.SideInterval;
                vm.WorkspaceLayout.SidePanelOpen = true;
                break;
            }
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is null && DataContext is RealtimeChartViewModel vm) vm.SetHoverTime(null);
    }

    private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not RealtimeChartViewModel vm) return;

        double factor = e.Delta > 0 ? 0.8 : 1.25;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            vm.ZoomYAxis(factor);
        }
        else
        {
            vm.ZoomTimeWindow(factor);
        }

        e.Handled = true;
    }
}
