using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MagnetometerSystem.App.Controls;

/// <summary>
/// 校正采集时的姿态覆盖预览：把三轴样本减去均值、按最大半径归一化后，画出指定两轴的投影。
/// 样本越接近整圆、分布越均匀，覆盖越好。只用于预览，不参与计算。
/// 样本集合变化时合并成一次重绘，连续采集不会每条读数都刷新一次。
/// </summary>
public class ProjectionPlot : FrameworkElement
{
    internal const int MaxDrawnPoints = 3000;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable), typeof(ProjectionPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty AxisAProperty = DependencyProperty.Register(
        nameof(AxisA), typeof(int), typeof(ProjectionPlot), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisBProperty = DependencyProperty.Register(
        nameof(AxisB), typeof(int), typeof(ProjectionPlot), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PointBrushProperty = DependencyProperty.Register(
        nameof(PointBrush), typeof(Brush), typeof(ProjectionPlot), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GuideBrushProperty = DependencyProperty.Register(
        nameof(GuideBrush), typeof(Brush), typeof(ProjectionPlot), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>样本集合，每项为 double[]（至少 3 个分量）。</summary>
    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public int AxisA { get => (int)GetValue(AxisAProperty); set => SetValue(AxisAProperty, value); }
    public int AxisB { get => (int)GetValue(AxisBProperty); set => SetValue(AxisBProperty, value); }
    public Brush PointBrush { get => (Brush)GetValue(PointBrushProperty); set => SetValue(PointBrushProperty, value); }
    public Brush GuideBrush { get => (Brush)GetValue(GuideBrushProperty); set => SetValue(GuideBrushProperty, value); }

    private bool _redrawPending;
    private INotifyCollectionChanged? _subscribed;

    public ProjectionPlot()
    {
        // 样本集合比控件活得久（ViewModel 持有）：只在加载期间订阅，卸载即退订，切页后旧控件不被集合事件留住。
        Loaded += (_, _) => Subscribe(Points);
        Unloaded += (_, _) => Unsubscribe();
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var plot = (ProjectionPlot)d;
        if (plot.IsLoaded) plot.Subscribe(e.NewValue as IEnumerable);
    }

    private void Subscribe(IEnumerable? points)
    {
        Unsubscribe();
        if (points is INotifyCollectionChanged list)
        {
            list.CollectionChanged += OnCollectionChanged;
            _subscribed = list;
        }
        InvalidateVisual(); // 卸载期间样本可能已变化
    }

    private void Unsubscribe()
    {
        if (_subscribed == null) return;
        _subscribed.CollectionChanged -= OnCollectionChanged;
        _subscribed = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_redrawPending) return;
        _redrawPending = true;
        Dispatcher.BeginInvoke(() => { _redrawPending = false; InvalidateVisual(); }, DispatcherPriority.Background);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 6;
        var guide = new Pen(GuideBrush, 1);
        guide.Freeze();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawEllipse(null, guide, center, radius, radius);
        dc.DrawLine(guide, new Point(center.X - radius - 4, center.Y), new Point(center.X + radius + 4, center.Y));
        dc.DrawLine(guide, new Point(center.X, center.Y - radius - 4), new Point(center.X, center.Y + radius + 4));

        // 只取至多 MaxDrawnPoints 个均匀抽样点（按下标直接读取，不复制整个集合）；
        // 中心与半径也按这些点估算——这是姿态覆盖预览，不参与拟合计算。
        var samples = Sample(Points);
        if (samples.Count == 0) return;

        double mx = 0, my = 0, mz = 0;
        foreach (var p in samples) { mx += p[0]; my += p[1]; mz += p[2]; }
        mx /= samples.Count; my /= samples.Count; mz /= samples.Count;
        var mean = new[] { mx, my, mz };
        double maxR = 0;
        foreach (var p in samples)
        {
            double dx = p[0] - mx, dy = p[1] - my, dz = p[2] - mz;
            maxR = Math.Max(maxR, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        if (maxR <= 0 || !double.IsFinite(maxR)) maxR = 1;

        int a = Math.Clamp(AxisA, 0, 2), b = Math.Clamp(AxisB, 0, 2);
        var dot = Math.Max(2.0, Math.Min(3.4, size / 40));
        foreach (var p in samples)
        {
            var x = center.X + (p[a] - mean[a]) / maxR * radius;
            var y = center.Y - (p[b] - mean[b]) / maxR * radius;
            if (double.IsFinite(x) && double.IsFinite(y)) dc.DrawEllipse(PointBrush, null, new Point(x, y), dot, dot);
        }
    }

    /// <summary>均匀抽取至多 <see cref="MaxDrawnPoints"/> 个三轴样本；可按下标访问的集合不做全量复制。</summary>
    internal static List<double[]> Sample(IEnumerable? points)
    {
        var result = new List<double[]>();
        if (points is IList list)
        {
            int count = list.Count;
            int step = Math.Max(1, (count + MaxDrawnPoints - 1) / MaxDrawnPoints);
            for (int i = 0; i < count; i += step)
                if (list[i] is double[] { Length: >= 3 } p) result.Add(p);
            return result;
        }
        if (points == null) return result;
        foreach (var item in points)
        {
            if (item is double[] { Length: >= 3 } p) result.Add(p);
            if (result.Count >= MaxDrawnPoints) break;
        }
        return result;
    }
}
