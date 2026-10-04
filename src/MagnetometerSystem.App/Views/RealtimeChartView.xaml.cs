using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class RealtimeChartView : UserControl
{
    private RealtimeChartViewModel? _boundViewModel;
    private readonly HashSet<Core.Models.ChannelDisplayConfig> _subscribedChannels = new();
    private readonly HashSet<Core.Models.ComputedChannelDefinition> _subscribedComputed = new();
    public RealtimeChartView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RealtimeChartViewModel vm)
        {
            if (ReferenceEquals(_boundViewModel, vm)) return;
            DetachViewModel();
            _boundViewModel = vm;
            vm.PlotControl = WpfPlot1;
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
        if (ReferenceEquals(vm.PlotControl, WpfPlot1))
        { vm.PlotControl = null; vm.MultiPlotControls.Clear(); }
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
        if (_boundViewModel is { IsMultiPlotMode: true } vm && (vm.IsChartHeightAutomatic || vm.WorkspaceLayout.IsFocused))
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
        vm.MultiPlotControls.Clear();

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
        if ((vm.IsChartHeightAutomatic || vm.WorkspaceLayout.IsFocused) && PlotArea.ActualHeight > 0)
            plotHeight = Math.Max(150, (PlotArea.ActualHeight - 8) / rowCount);

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
            ChartFontHelper.Apply(wpfPlot.Plot);

            System.Windows.Controls.Grid.SetRow(wpfPlot, row);
            System.Windows.Controls.Grid.SetColumn(wpfPlot, col);

            grid.Children.Add(wpfPlot);
            vm.MultiPlotControls.Add(wpfPlot);

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
            ChartFontHelper.Apply(wpfPlot.Plot);

            System.Windows.Controls.Grid.SetRow(wpfPlot, row);
            System.Windows.Controls.Grid.SetColumn(wpfPlot, col);

            grid.Children.Add(wpfPlot);
            vm.MultiPlotControls.Add(wpfPlot);

            channelIndex++;
        }

        MultiPlotPanel.Children.Add(grid);
        vm.RefreshPlot();
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
