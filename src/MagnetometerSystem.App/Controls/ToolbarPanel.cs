using System.Windows;
using System.Windows.Controls;

namespace MagnetometerSystem.App.Controls;

/// <summary>
/// 工具栏两组控件：第一个子元素靠左，第二个靠右。一行放得下时同一行左右对齐，
/// 放不下时第二组换到下一行（仍靠右），不截掉任何一组。
/// </summary>
public class ToolbarPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ToolbarPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>同一行时两组之间的最小间距，换行时两行之间的间距。</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>上一次排布是否换成了两行（供测试与诊断）。</summary>
    public bool IsWrapped { get; private set; }

    private (UIElement? Left, UIElement? Right) Groups() => (
        InternalChildren.Count > 0 ? InternalChildren[0] : null,
        InternalChildren.Count > 1 ? InternalChildren[1] : null);

    protected override Size MeasureOverride(Size availableSize)
    {
        var (left, right) = Groups();
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        left?.Measure(unbounded);
        right?.Measure(unbounded);
        var l = left?.DesiredSize ?? default;
        var r = right?.DesiredSize ?? default;
        IsWrapped = right != null && left != null && l.Width + Spacing + r.Width > availableSize.Width;
        if (!IsWrapped)
            return new Size(Math.Min(availableSize.Width, l.Width + (right != null ? Spacing + r.Width : 0)), Math.Max(l.Height, r.Height));
        // 换行后左组仍可能比可用宽度宽（极窄时），按可用宽度重新测量，由子元素自行裁剪。
        left!.Measure(new Size(availableSize.Width, availableSize.Height));
        return new Size(Math.Min(availableSize.Width, Math.Max(left.DesiredSize.Width, r.Width)), left.DesiredSize.Height + Spacing + r.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (left, right) = Groups();
        if (!IsWrapped)
        {
            double h = finalSize.Height;
            if (left != null)
                left.Arrange(new Rect(0, (h - left.DesiredSize.Height) / 2, Math.Min(left.DesiredSize.Width, finalSize.Width), left.DesiredSize.Height));
            if (right != null)
                right.Arrange(new Rect(Math.Max(0, finalSize.Width - right.DesiredSize.Width), (h - right.DesiredSize.Height) / 2,
                    right.DesiredSize.Width, right.DesiredSize.Height));
        }
        else
        {
            left!.Arrange(new Rect(0, 0, Math.Min(left.DesiredSize.Width, finalSize.Width), left.DesiredSize.Height));
            right!.Arrange(new Rect(Math.Max(0, finalSize.Width - right.DesiredSize.Width), left.DesiredSize.Height + Spacing,
                right.DesiredSize.Width, right.DesiredSize.Height));
        }
        return finalSize;
    }
}
