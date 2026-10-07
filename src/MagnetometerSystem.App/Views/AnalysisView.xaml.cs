using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.App.Views;

public partial class AnalysisView : UserControl
{
    private const int MaxPlotPoints = 4000;
    private AnalysisViewModel? _vm;

    public AnalysisView()
    {
        InitializeComponent();
        ChartFontHelper.Apply(Plot.Plot);
        DataContextChanged += (_, _) => Attach(DataContext as AnalysisViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => { Attach(DataContext as AnalysisViewModel); Render(); };
    }

    private void Attach(AnalysisViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisViewModel.FocusedResult)) Render();
    }

    private void OnChartModeChanged(object sender, RoutedEventArgs e) => Render();

    /// <summary>
    /// 时域模式：原始值（降采样显示）、拟合趋势线、分段均值。
    /// 噪声模式：每个噪声窗口的残差标准差随时间的变化。
    /// </summary>
    private void Render()
    {
        if (Plot == null) return;
        var plot = Plot.Plot;
        plot.Clear();
        ChartFontHelper.Apply(plot);
        var row = _vm?.FocusedResult;
        if (row == null || row.Seconds.Length == 0) { Plot.Refresh(); return; }
        var r = row.Result;
        var accent = ScottPlot.Color.FromHex("#2A78D6");
        var orange = ScottPlot.Color.FromHex("#EB6834");
        var green = ScottPlot.Color.FromHex("#1BAF7A");

        if (ShowNoise.IsChecked == true)
        {
            var xs = r.NoiseWindows.Select(w => w.StartSeconds).ToArray();
            var ys = r.NoiseWindows.Select(w => w.StdDev).ToArray();
            if (xs.Length > 0)
            {
                var s = plot.Add.Scatter(xs, ys);
                s.Color = accent; s.LineWidth = 1.5f; s.MarkerSize = xs.Length > 200 ? 0 : 4;
                s.LegendText = "窗口残差标准差" + (r.NoiseWindowCount > r.NoiseWindows.Count
                    ? $"（显示 {r.NoiseWindows.Count:N0} / {r.NoiseWindowCount:N0} 个窗口）" : "");
                var median = plot.Add.HorizontalLine(r.NoiseMedianStd);
                median.Color = orange; median.LineWidth = 1.5f; median.LinePattern = ScottPlot.LinePattern.Dashed;
                median.LegendText = $"中位数 {r.NoiseMedianStd:G5}";
            }
            plot.Axes.Left.Label.Text = $"标准差 ({row.Unit})";
        }
        else
        {
            // 直接从源数组按桶取极值并跳过非有限值，不在界面线程上复制全量数据。
            var (xs, ys) = MinMaxDecimator.Decimate(row.Seconds, row.Values, MaxPlotPoints);
            if (xs.Length > 0)
            {
                var series = plot.Add.ScatterLine(xs, ys);
                series.Color = accent.WithAlpha(.85); series.LineWidth = 1f;
                series.LegendText = "原始值" + (row.Values.Length > MaxPlotPoints ? "（显示降采样）" : "");
            }
            if (double.IsFinite(r.DriftPerHour) && xs.Length > 1)
            {
                double x0 = xs[0], x1 = xs[^1], k = r.DriftPerHour / 3600;
                var trend = plot.Add.Line(x0, r.TrendIntercept + k * x0, x1, r.TrendIntercept + k * x1);
                trend.Color = orange; trend.LineWidth = 2f;
                trend.LegendText = $"趋势 {r.DriftPerHour:G4} {row.Unit}/h";
            }
            if (r.Segments.Count > 0)
            {
                var mx = r.Segments.Select(s => (s.StartSeconds + s.EndSeconds) / 2).ToArray();
                var my = r.Segments.Select(s => s.Mean).ToArray();
                var seg = plot.Add.Scatter(mx, my);
                seg.Color = green; seg.LineWidth = 0; seg.MarkerSize = 7;
                seg.LegendText = "分段均值";
            }
            plot.Axes.Left.Label.Text = $"{row.Name} ({row.Unit})";
        }
        plot.Axes.Bottom.Label.Text = "时间（相对所选起点，s）";
        plot.ShowLegend();
        plot.Axes.AutoScale();
        Plot.Refresh();
    }
}
