using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>辅助面板的布局状态；折叠仅影响显示，不停止采集或重置内容。</summary>
public partial class WorkspaceLayoutViewModel : ObservableObject
{
    [ObservableProperty] private bool _connectionExpanded;
    [ObservableProperty] private bool _storageExpanded;
    [ObservableProperty] private bool _channelsExpanded;
    [ObservableProperty] private bool _analysisExpanded;
    [ObservableProperty] private bool _terminalExpanded;
    [ObservableProperty] private bool _isFocused;

    private Dictionary<string, bool>? _beforeFocus;
    private bool _restoring;

    // A compact window scrolls the expanded controls instead of squeezing the plot away.
    public double MinimumWorkspaceHeight => 640
        + (ConnectionExpanded ? 110 : 0) + (StorageExpanded ? 70 : 0)
        + (ChannelsExpanded ? 150 : 0) + (AnalysisExpanded ? 200 : 0)
        + (TerminalExpanded ? 160 : 0);

    partial void OnConnectionExpandedChanged(bool value) => OnPanelChanged();
    partial void OnStorageExpandedChanged(bool value) => OnPanelChanged();
    partial void OnChannelsExpandedChanged(bool value) => OnPanelChanged();
    partial void OnAnalysisExpandedChanged(bool value) => OnPanelChanged();
    partial void OnTerminalExpandedChanged(bool value) => OnPanelChanged();

    private void OnPanelChanged()
    {
        OnPropertyChanged(nameof(MinimumWorkspaceHeight));
        if (_restoring || !IsFocused) return;
        IsFocused = false;
        _beforeFocus = null;
    }

    public Dictionary<string, bool> GetPersistedPanels() => _beforeFocus is not null
        ? new(_beforeFocus)
        : new()
        {
            ["connection"] = ConnectionExpanded,
            ["storage"] = StorageExpanded,
            ["channels"] = ChannelsExpanded,
            ["analysis"] = AnalysisExpanded,
            ["terminal"] = TerminalExpanded,
        };

    public void Restore(IReadOnlyDictionary<string, bool>? panels)
    {
        if (panels is null) return;
        _restoring = true;
        try
        {
            ConnectionExpanded = panels.GetValueOrDefault("connection");
            StorageExpanded = panels.GetValueOrDefault("storage");
            ChannelsExpanded = panels.GetValueOrDefault("channels");
            AnalysisExpanded = panels.GetValueOrDefault("analysis");
            TerminalExpanded = panels.GetValueOrDefault("terminal");
        }
        finally { _restoring = false; }
    }

    [RelayCommand]
    private void ToggleFocus()
    {
        if (IsFocused)
        {
            Restore(_beforeFocus);
            _beforeFocus = null;
            IsFocused = false;
        }
        else
        {
            _beforeFocus = GetPersistedPanels();
            Restore(new Dictionary<string, bool>());
            IsFocused = true;
        }
    }
}
