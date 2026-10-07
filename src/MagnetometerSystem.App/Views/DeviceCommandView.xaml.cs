using System.Collections.Specialized;
using System.Windows.Controls;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class DeviceCommandView : UserControl
{
    private DeviceCommandViewModel? _vm;

    public DeviceCommandView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DeviceCommandViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as DeviceCommandViewModel);
    }

    private void Attach(DeviceCommandViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.TrafficEntries.CollectionChanged -= OnTrafficChanged;
        _vm = vm;
        if (_vm != null) _vm.TrafficEntries.CollectionChanged += OnTrafficChanged;
    }

    private void OnTrafficChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 大批量刷新以 Reset 整体替换，同样跟到最新一条。
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset) || _vm is null or { PauseAutoScroll: true }) return;
        if (TrafficList.Items.Count > 0 && TrafficList.IsVisible) TrafficList.ScrollIntoView(TrafficList.Items[^1]);
    }
}
