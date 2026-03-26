using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class RealtimeChartView : UserControl
{
    public RealtimeChartView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RealtimeChartViewModel vm)
        {
            vm.PlotControl = WpfPlot1;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RealtimeChartViewModel.IsMultiPlotMode) or
            nameof(RealtimeChartViewModel.MultiPlotColumnCount))
        {
            RebuildMultiPlotControls();
        }
    }

    private void RebuildMultiPlotControls()
    {
        if (DataContext is not RealtimeChartViewModel vm) return;

        MultiPlotPanel.Children.Clear();
        vm.MultiPlotControls.Clear();

        if (!vm.IsMultiPlotMode) return;

        // 统计可见通道数
        int visibleCount = vm.ChannelConfigs.Count(c => c.Visible);
        if (visibleCount == 0) return;

        int columnCount = vm.MultiPlotColumnCount;
        int rowCount = (int)Math.Ceiling((double)visibleCount / columnCount);
        double plotHeight = Math.Max(120, 400.0 / Math.Max(rowCount, 1));

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

        int plotIndex = 0;
        foreach (var config in vm.ChannelConfigs)
        {
            if (!config.Visible) continue;

            int row = plotIndex / columnCount;
            int col = plotIndex % columnCount;

            var wpfPlot = new ScottPlot.WPF.WpfPlot
            {
                Margin = new System.Windows.Thickness(2),
            };
            wpfPlot.MouseWheel += OnPlotMouseWheel;

            System.Windows.Controls.Grid.SetRow(wpfPlot, row);
            System.Windows.Controls.Grid.SetColumn(wpfPlot, col);

            grid.Children.Add(wpfPlot);
            vm.MultiPlotControls.Add(wpfPlot);

            plotIndex++;
        }

        MultiPlotPanel.Children.Add(grid);
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
