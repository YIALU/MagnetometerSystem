using System.Windows;
using System.Windows.Controls;

namespace MagnetometerSystem.App.Controls;

/// <summary>
/// 横向排列子元素，宽度按附加属性 <see cref="WeightProperty"/> 成比例分配（用于帧结构字节图）。
/// 每个子元素至少 <see cref="MinItemWidth"/>，总宽不够时等比压缩其余部分。
/// </summary>
public class WeightPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(WeightPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(DependencyObject d) => (double)d.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject d, double value) => d.SetValue(WeightProperty, value);

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(WeightPanel),
        new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    private static double WeightOf(UIElement child)
    {
        // ItemsControl 会把元素包在 ContentPresenter 里，权重设在模板根元素上时向内取一层。
        var w = GetWeight(child);
        if (child is ContentPresenter { } cp && System.Windows.Media.VisualTreeHelper.GetChildrenCount(cp) > 0
            && System.Windows.Media.VisualTreeHelper.GetChild(cp, 0) is UIElement inner && inner.ReadLocalValue(WeightProperty) != DependencyProperty.UnsetValue)
            w = GetWeight(inner);
        return Math.Max(0.0001, w);
    }

    private double[] Widths(double available)
    {
        var children = InternalChildren.Cast<UIElement>().ToArray();
        if (children.Length == 0) return [];
        var weights = children.Select(WeightOf).ToArray();
        if (double.IsInfinity(available)) return weights.Select(w => Math.Max(MinItemWidth, w * 24)).ToArray();
        var total = weights.Sum();
        var widths = weights.Select(w => available * w / total).ToArray();
        // 先给过窄的元素补到最小宽度，再从其余元素等比扣回。
        var small = widths.Select(w => w < MinItemWidth).ToArray();
        var deficit = widths.Where((w, i) => small[i]).Sum(w => MinItemWidth - w);
        var rest = widths.Where((w, i) => !small[i]).Sum();
        for (int i = 0; i < widths.Length; i++)
            widths[i] = small[i] ? MinItemWidth : rest > 0 ? widths[i] - deficit * widths[i] / rest : widths[i];
        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = Widths(availableSize.Width);
        double height = 0, sum = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            sum += widths[i++];
        }
        return new Size(double.IsInfinity(availableSize.Width) ? sum : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Widths(finalSize.Width);
        double x = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, Math.Max(0, widths[i]), finalSize.Height));
            x += widths[i++];
        }
        return finalSize;
    }
}
