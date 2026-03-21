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
        if (e.PropertyName is nameof(RealtimeChartViewModel.IsMultiPlotMode))
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

        // 为每个可见通道创建独立图表
        int visibleCount = 0;
        foreach (var config in vm.ChannelConfigs)
        {
            if (config.Visible) visibleCount++;
        }

        double plotHeight = Math.Max(120, 400.0 / Math.Max(visibleCount, 1));

        foreach (var config in vm.ChannelConfigs)
        {
            if (!config.Visible) continue;

            var wpfPlot = new ScottPlot.WPF.WpfPlot
            {
                Height = plotHeight,
                Margin = new System.Windows.Thickness(0, 0, 0, 2),
            };
            wpfPlot.MouseWheel += OnPlotMouseWheel;
            MultiPlotPanel.Children.Add(wpfPlot);
            vm.MultiPlotControls.Add(wpfPlot);
        }
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
