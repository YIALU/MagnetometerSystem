using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using ScottPlot;
using ScottPlot.WPF;

namespace MagnetometerSystem.App.Views.Charting;

/// <summary>
/// 把 <see cref="RealtimeChartViewModel"/> 每次刷新算好的 <see cref="ChartFrame"/> 画到视图持有的 ScottPlot 控件上，
/// 并维护区间阴影、拖动预览和十字准线这些叠加层。只画，不取数也不计算曲线。
/// 由 <see cref="RealtimeChartView"/> 创建，与它的单图控件同生命周期：视图加载时接上视图模型，卸载时解除，
/// 解除后视图模型不再留住这些图及其曲线数据。
/// </summary>
public sealed class ChartRenderer : IDisposable
{
    private static readonly Color OverlayAccent = Color.FromHex("#2456C2");
    private static readonly Color OverlayMuted = Color.FromHex("#66726E");

    private RealtimeChartViewModel? _vm;
    private readonly List<WpfPlot> _multiPlots = new();
    // 单图的单位轴：第一个单位用左轴，其余各加一条右轴；下次绘制先去掉上次加的右轴。
    private readonly Dictionary<string, IYAxis> _unitAxes = new();
    // 每张图当前的叠加层；鼠标移动时只替换这些并重绘，不重画曲线。
    private readonly Dictionary<Plot, (ChannelDisplayConfig? Channel, List<IPlottable> Items)> _overlays = new();

    public ChartRenderer(WpfPlot singlePlot) => SinglePlot = singlePlot;

    /// <summary>单图模式的图。</summary>
    public WpfPlot SinglePlot { get; }

    /// <summary>多图模式的各张图，按显示顺序。</summary>
    public IReadOnlyList<WpfPlot> MultiPlots => _multiPlots;

    /// <summary>仍保留叠加层记录的图数量（诊断与测试用）。</summary>
    internal int OverlayPlotCount => _overlays.Count;

    /// <summary>开始画这个视图模型的帧；之前接上的视图模型先解除。</summary>
    public void Attach(RealtimeChartViewModel vm)
    {
        Detach();
        _vm = vm;
        vm.FrameReady += Render;
        vm.ChartStarted += OnChartStarted;
        vm.ChartCleared += OnChartCleared;
        vm.OverlaysChanged += RefreshOverlays;
    }

    /// <summary>不再画：解除订阅，丢掉多图和叠加层记录。</summary>
    public void Detach()
    {
        if (_vm is { } vm)
        {
            vm.FrameReady -= Render;
            vm.ChartStarted -= OnChartStarted;
            vm.ChartCleared -= OnChartCleared;
            vm.OverlaysChanged -= RefreshOverlays;
            _vm = null;
        }
        _multiPlots.Clear();
        _overlays.Clear();
    }

    /// <summary>同 <see cref="Detach"/>。</summary>
    public void Dispose() => Detach();

    /// <summary>多图重建后换上新的一组图（按显示顺序）；不再显示的旧图连同叠加层记录一并丢掉。</summary>
    public void SetMultiPlots(IEnumerable<WpfPlot> plots)
    {
        _multiPlots.Clear();
        _multiPlots.AddRange(plots);
        var live = _multiPlots.Select(p => p.Plot).Append(SinglePlot.Plot).ToHashSet();
        foreach (var stale in _overlays.Keys.Where(p => !live.Contains(p)).ToArray()) _overlays.Remove(stale);
    }

    private void Render(ChartFrame frame)
    {
        if (frame.Axes is not { } axes)
        {
            // 时间窗口内还没有数据：不清掉已画的内容，单图只刷新一次。
            if (!frame.IsMultiPlot) SinglePlot.Refresh();
            return;
        }

        if (frame.IsMultiPlot)
            RenderMultiPlot(frame.Panels, axes);
        else
            RenderSinglePlot(frame, axes);
    }

    private void RenderSinglePlot(ChartFrame frame, ChartAxes axes)
    {
        var plot = SinglePlot.Plot;
        plot.Clear();
        ConfigureUnitAxes(plot, frame.Units);

        foreach (var curve in frame.Curves)
        {
            var line = AddCurve(plot, curve);
            if (curve.Unit is { } unit && _unitAxes.TryGetValue(unit, out var axis))
                line.Axes.YAxis = axis;
        }

        ConfigurePlotAxes(plot, axes);
        // 无参 AutoScaleY 只调整左轴；独立单位轴始终按自身数据确定范围。
        foreach (var axis in _unitAxes.Values.Where(a => !ReferenceEquals(a, plot.Axes.Left)))
            plot.Axes.AutoScaleY(axis);
        plot.ShowLegend();
        AddOverlays(plot, null);
        SinglePlot.Refresh();
    }

    private void ConfigureUnitAxes(Plot plot, IReadOnlyList<string> units)
    {
        foreach (var old in _unitAxes.Values.Where(a => !ReferenceEquals(a, plot.Axes.Left)))
            plot.Axes.Remove(old);
        _unitAxes.Clear();
        plot.Axes.Left.Label.Text = units.FirstOrDefault() ?? "数值";
        foreach (var unit in units)
        {
            IYAxis axis = _unitAxes.Count == 0 ? plot.Axes.Left : plot.Axes.AddRightAxis();
            axis.Label.Text = unit;
            _unitAxes.Add(unit, axis);
        }
    }

    /// <summary>各张图按顺序画到视图建好的图上；视图按可见通道与启用的计算通道建图，没有图的内容不画。</summary>
    private void RenderMultiPlot(IReadOnlyList<ChartPanel> panels, ChartAxes axes)
    {
        for (int i = 0; i < panels.Count && i < _multiPlots.Count; i++)
        {
            var panel = panels[i];
            var plotCtrl = _multiPlots[i];
            var plot = plotCtrl.Plot;
            plot.Clear();

            if (panel.Curve is { } curve)
                AddCurve(plot, curve);

            // 图上统计标注（右上角）
            if (panel.Statistics is { } statistics)
            {
                var ann = plot.Add.Annotation(statistics, Alignment.UpperRight);
                ann.LabelFontSize = 10;
                ann.LabelFontName = ChartFontHelper.DefaultCjkFont;
                ann.LabelBackgroundColor = new Color(255, 255, 255, 200);
                ann.LabelBorderColor = new Color(200, 200, 200, 255);
                ann.LabelBorderWidth = 1;
            }

            plot.Axes.Left.Label.Text = panel.AxisLabel;
            ConfigurePlotAxes(plot, axes);
            AddOverlays(plot, panel.Channel);
            plotCtrl.Refresh();
        }
    }

    private static ScottPlot.Plottables.Scatter AddCurve(Plot plot, ChartCurve curve)
    {
        var line = plot.Add.ScatterLine(curve.Xs, curve.Ys);
        if (curve.Color is { } c)
            line.Color = new Color(c.R, c.G, c.B, c.A);
        line.LineWidth = curve.LineWidth;
        if (curve.Legend is not null)
            line.LegendText = curve.Legend;
        return line;
    }

    private static void ConfigurePlotAxes(Plot plot, ChartAxes axes)
    {
        if (axes.AutoScroll)
            plot.Axes.SetLimitsX(axes.XMin, axes.XMax);

        if (axes.AutoScaleY)
            plot.Axes.AutoScaleY();
        else
            plot.Axes.SetLimitsY(axes.YMin, axes.YMax);

        plot.Axes.Bottom.Label.Text = "时间 (s)";
        plot.Grid.IsVisible = axes.ShowGrid;
    }

    private void OnChartStarted()
    {
        var plot = SinglePlot.Plot;
        plot.Clear();
        plot.Axes.Bottom.Label.Text = "时间 (s)";
        plot.Axes.Left.Label.Text = "数值（单位由协议定义）";
        SinglePlot.Refresh();
    }

    private void OnChartCleared()
    {
        foreach (var control in _multiPlots)
        {
            control.Plot.Clear();
            control.Refresh();
        }

        SinglePlot.Plot.Clear();
        SinglePlot.Refresh();
    }

    // ---- 叠加层：区间阴影、拖动预览、十字准线 ----
    // 只画在曲线上，不影响数据、保存或统计。

    /// <summary>在一张图上加区间阴影、拖动预览和十字准线读数。channel 为多图模式下这张图对应的通道。</summary>
    private void AddOverlays(Plot plot, ChannelDisplayConfig? channel)
    {
        if (_vm is not { } vm) return;
        var overlay = vm.Overlay;
        var items = new List<IPlottable>();
        if (overlay.Interval is { } interval)
        {
            var span = plot.Add.VerticalSpan(interval.Start, interval.End);
            span.FillStyle.Color = OverlayAccent.WithAlpha(.10);
            span.LineStyle.Color = OverlayAccent.WithAlpha(.45);
            span.LineStyle.Width = 1;
            items.Add(span);
        }
        if (overlay.Drag is { } drag)
        {
            var preview = plot.Add.VerticalSpan(drag.Start, drag.End);
            preview.FillStyle.Color = OverlayAccent.WithAlpha(.22);
            preview.LineStyle.Width = 0;
            items.Add(preview);
        }
        if (overlay.Crosshair is { } t)
        {
            var line = plot.Add.VerticalLine(t);
            line.Color = OverlayMuted.WithAlpha(.8);
            line.LineWidth = 1;
            line.LinePattern = LinePattern.Dashed;
            items.Add(line);
            if (vm.CrosshairText(channel) is { } text)
            {
                var ann = plot.Add.Annotation(text, channel is null ? Alignment.UpperRight : Alignment.LowerLeft);
                ann.LabelFontSize = 11;
                ann.LabelFontName = ChartFontHelper.DefaultCjkFont;
                ann.LabelBackgroundColor = new Color(255, 255, 255, 225);
                ann.LabelBorderColor = Color.FromHex("#D2D9D5");
                ann.LabelBorderWidth = 1;
                items.Add(ann);
            }
        }
        _overlays[plot] = (channel, items);
    }

    /// <summary>只替换叠加层并重绘，不重新取数。</summary>
    private void RefreshOverlays()
    {
        foreach (var (plot, (channel, items)) in _overlays.ToArray())
        {
            foreach (var item in items) plot.Remove(item);
            AddOverlays(plot, channel);
        }
        SinglePlot.Refresh();
        foreach (var control in _multiPlots) control.Refresh();
    }
}
