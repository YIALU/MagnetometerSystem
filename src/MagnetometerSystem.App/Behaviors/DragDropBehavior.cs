using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Behaviors;

public class DragDropBehavior : Behavior<Panel>
{
    private UIElement? _draggedElement;
    private Point _startPoint;
    private int _draggedIndex = -1;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove += OnPreviewMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        AssociatedObject.Drop += OnDrop;
        AssociatedObject.DragOver += OnDragOver;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove -= OnPreviewMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        AssociatedObject.Drop -= OnDrop;
        AssociatedObject.DragOver -= OnDragOver;
        base.OnDetaching();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(AssociatedObject);
        var element = FindChartElement(e.OriginalSource as DependencyObject);
        if (element != null)
        {
            _draggedElement = element;
            _draggedIndex = GetElementIndex(element);
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedElement == null) return;

        var currentPoint = e.GetPosition(AssociatedObject);
        if (Math.Abs(currentPoint.X - _startPoint.X) < 10 && Math.Abs(currentPoint.Y - _startPoint.Y) < 10) return;

        DragDrop.DoDragDrop(_draggedElement, _draggedIndex, DragDropEffects.Move);
        _draggedElement = null;
        _draggedIndex = -1;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedElement = null;
        _draggedIndex = -1;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int))) return;

        int sourceIndex = (int)e.Data.GetData(typeof(int));
        var dropTarget = FindChartElement(e.OriginalSource as DependencyObject);
        if (dropTarget == null) return;

        int targetIndex = GetElementIndex(dropTarget);
        if (sourceIndex == targetIndex || sourceIndex < 0 || targetIndex < 0) return;

        var vm = GetViewModel();
        if (vm != null)
        {
            vm.ReorderChannels(sourceIndex, targetIndex);
        }
    }

    private UIElement? FindChartElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ScottPlot.WPF.WpfPlot wpfPlot)
                return wpfPlot;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private int GetElementIndex(UIElement element)
    {
        if (AssociatedObject.Children.Count == 0) return -1;
        var grid = AssociatedObject.Children[0] as Grid;
        if (grid == null) return -1;
        return grid.Children.IndexOf(element);
    }

    private RealtimeChartViewModel? GetViewModel()
    {
        DependencyObject? view = AssociatedObject;
        while (view != null)
        {
            if (view is FrameworkElement fe && fe.DataContext is RealtimeChartViewModel vm)
                return vm;
            view = VisualTreeHelper.GetParent(view);
        }
        return null;
    }
}
