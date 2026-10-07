using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>工作台辅助区域的布局；折叠和专注只影响显示，不停止采集、保存或重置内容。</summary>
public partial class WorkspaceLayoutViewModel : ObservableObject
{
    public const int DockTraffic = 0, DockRawFrames = 1, DockEvents = 2;
    public const int SideChannels = 0, SideStatistics = 1, SideInterval = 2, SideFilter = 3, SideCorrection = 4;

    [ObservableProperty] private bool _sidePanelOpen = true;
    [ObservableProperty] private bool _dockOpen = true;
    [ObservableProperty] private int _sideTab;
    [ObservableProperty] private int _dockTab;
    [ObservableProperty] private bool _isFocused;

    private Dictionary<string, bool>? _beforeFocus;
    private bool _restoring;

    partial void OnSidePanelOpenChanged(bool value) => OnPanelChanged(value);
    partial void OnDockOpenChanged(bool value) => OnPanelChanged(value);

    private void OnPanelChanged(bool opened)
    {
        // 专注模式下手动打开任一区域即退出专注，但不连带打开其他区域。
        if (_restoring || !IsFocused || !opened) return;
        IsFocused = false;
        _beforeFocus = null;
    }

    public Dictionary<string, bool> GetPersistedPanels() => _beforeFocus is not null
        ? new(_beforeFocus)
        : new() { ["sidePanel"] = SidePanelOpen, ["dock"] = DockOpen };

    public void Restore(IReadOnlyDictionary<string, bool>? panels)
    {
        if (panels is null) return;
        _restoring = true;
        try
        {
            // 旧版本保存的是逐个折叠面板，键不同时保持新布局默认展开。
            SidePanelOpen = panels.TryGetValue("sidePanel", out var side) ? side : true;
            DockOpen = panels.TryGetValue("dock", out var dock) ? dock : true;
        }
        finally { _restoring = false; }
    }

    /// <summary>打开底部停靠区的指定页（例如从链路条的“异常”跳到原始报文）。</summary>
    public void ShowDock(int tab)
    {
        DockTab = tab;
        DockOpen = true;
    }

    [RelayCommand]
    private void ToggleFocus()
    {
        if (IsFocused)
        {
            _restoring = true;
            try
            {
                SidePanelOpen = _beforeFocus?.GetValueOrDefault("sidePanel", true) ?? true;
                DockOpen = _beforeFocus?.GetValueOrDefault("dock", true) ?? true;
            }
            finally { _restoring = false; }
            _beforeFocus = null;
            IsFocused = false;
        }
        else
        {
            _beforeFocus = GetPersistedPanels();
            _restoring = true;
            try { SidePanelOpen = false; DockOpen = false; }
            finally { _restoring = false; }
            IsFocused = true;
        }
    }

    [RelayCommand]
    private void ToggleDock() => DockOpen = !DockOpen;

    [RelayCommand]
    private void ToggleSidePanel() => SidePanelOpen = !SidePanelOpen;
}
