using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class AcquisitionWorkspaceView : UserControl
{
    private MainViewModel? _vm;
    private INotifyCollectionChanged? _rawLines;
    private double _dockHeight = 230;

    public AcquisitionWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.WorkspaceLayout.PropertyChanged -= OnLayoutChanged;
        if (_rawLines != null) _rawLines.CollectionChanged -= OnRawLinesChanged;
        if (_records != null) _records.CollectionChanged -= OnRecordsChanged;
        if (_traffic != null) _traffic.CollectionChanged -= OnTrafficChanged;
        _vm = vm;
        _rawLines = vm?.ConnectionVM.RawDataLines;
        _records = vm?.ConnectionVM.ParseRecords;
        _traffic = vm?.DeviceCommandVM.TrafficEntries;
        if (vm == null) return;
        vm.WorkspaceLayout.PropertyChanged += OnLayoutChanged;
        if (_rawLines != null) _rawLines.CollectionChanged += OnRawLinesChanged;
        if (_records != null) _records.CollectionChanged += OnRecordsChanged;
        if (_traffic != null) _traffic.CollectionChanged += OnTrafficChanged;
        ApplyLayout();
    }

    private INotifyCollectionChanged? _records, _traffic;

    /// <summary>新增或合并到最后一行时滚到底部。</summary>
    private void OnRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && RecordList.Items.Count > 0 && RecordList.IsVisible)
            RecordList.ScrollIntoView(RecordList.Items[^1]);
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceLayoutViewModel.DockOpen) or nameof(WorkspaceLayoutViewModel.SidePanelOpen))
            ApplyLayout();
    }

    /// <summary>
    /// 停靠区高度由分隔条调整（本地值），收起时记住高度，展开时恢复；
    /// 侧栏收起时连同间距一起让出宽度给曲线。
    /// </summary>
    private void ApplyLayout()
    {
        if (_vm == null) return;
        var layout = _vm.WorkspaceLayout;
        if (layout.DockOpen)
        {
            DockRow.Height = new GridLength(_dockHeight);
            DockRow.MinHeight = 140;
            DockSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (DockRow.ActualHeight > 140) _dockHeight = DockRow.ActualHeight;
            DockRow.MinHeight = 0;
            DockRow.Height = GridLength.Auto;
            DockSplitter.Visibility = Visibility.Collapsed;
        }
        SidePanel.Visibility = layout.SidePanelOpen ? Visibility.Visible : Visibility.Collapsed;
        SideColumn.Width = layout.SidePanelOpen ? new GridLength(350) : new GridLength(0);
        SideGapColumn.Width = layout.SidePanelOpen ? new GridLength(12) : new GridLength(0);
    }

    private void DockTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is { WorkspaceLayout.DockOpen: false }) _vm.WorkspaceLayout.DockOpen = true;
    }

    private void OnTrafficChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm?.DeviceCommandVM.PauseAutoScroll != true
            && TrafficLog.Items.Count > 0 && TrafficLog.IsVisible)
            TrafficLog.ScrollIntoView(TrafficLog.Items[^1]);
    }

    private void OnRawLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && RawList.Items.Count > 0 && RawList.IsVisible)
            RawList.ScrollIntoView(RawList.Items[^1]);
    }
}
