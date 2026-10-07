using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>导出时按名称勾选的通道。</summary>
public partial class ExportChannelOption : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// “数据”页：会话列表与历史回放合在一页。选中会话后可直接回放或导出；
/// 回放只发布显示流，连接期间（含 TCP 重连）不能打开回放。
/// </summary>
public partial class DataPageViewModel : ObservableObject
{
    private readonly DataBus _dataBus;
    private string? _exportSessionId;

    public SessionListViewModel Sessions { get; }
    public HistoryPlaybackViewModel Playback { get; }

    /// <summary>导出通道（按会话记录的名称和单位）；全选时导出全部通道。</summary>
    public ObservableCollection<ExportChannelOption> ExportChannels { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedPlaybackCommand))]
    private bool _isLiveConnected;

    [ObservableProperty]
    private bool _isPlaybackOpen;

    [ObservableProperty]
    private bool _isExportOpen;

    public DataPageViewModel(SessionListViewModel sessions, HistoryPlaybackViewModel playback, DataBus dataBus)
    {
        Sessions = sessions;
        Playback = playback;
        _dataBus = dataBus;
        _isLiveConnected = dataBus.CurrentConnection != null;
        _dataBus.ConnectionChanged += OnConnectionChanged;
        Sessions.PropertyChanged += OnSessionsPropertyChanged;
    }

    public SessionInfo? Selected => Sessions.SelectedSession;
    public bool HasSelection => Sessions.SelectedSession != null;
    public bool IsSelectedLegacy => Sessions.SelectedSession?.LegacyDataTable != null;
    public bool IsSelectedActive => Sessions.SelectedSession is { } s && s.Id == Sessions.ActiveSessionId;

    // 时长按 UTC 时刻计算，跨夏令时切换不会多或少一小时。
    public string SelectedDurationText => Sessions.SelectedSession is { } s
        ? s.Duration is { } duration ? FormatDuration(duration) : "进行中"
        : "";

    /// <summary>不能回放时说明原因；能回放时为空。</summary>
    public string PlaybackBlockedReason =>
        IsLiveConnected ? "连接期间不能回放。断开后可回放；回放只用于查看，不写入任何会话。"
        : IsSelectedActive ? "该会话正在采集，结束后才能回放。"
        : IsSelectedLegacy ? "旧格式数据需要迁移后才能回放和导出，原始表保留未删除。"
        : "";

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionListViewModel.SelectedSession))
        {
            RebuildExportChannels();
            OnSelectionChanged();
        }
        else if (e.PropertyName is nameof(SessionListViewModel.ActiveSessionId))
            OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectedLegacy));
        OnPropertyChanged(nameof(IsSelectedActive));
        OnPropertyChanged(nameof(SelectedDurationText));
        OnPropertyChanged(nameof(PlaybackBlockedReason));
        OpenSelectedPlaybackCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLiveConnectedChanged(bool value) => OnPropertyChanged(nameof(PlaybackBlockedReason));

    private void RebuildExportChannels()
    {
        // 会话列表刷新会换成同一会话的新对象；此时保留已勾选的导出通道。
        var keep = Sessions.SelectedSession is { } next && next.Id == _exportSessionId
            ? ExportChannels.ToDictionary(c => c.Index, c => c.IsSelected)
            : null;
        foreach (var option in ExportChannels) option.PropertyChanged -= OnExportChannelChanged;
        ExportChannels.Clear();
        _exportSessionId = Sessions.SelectedSession?.Id;
        if (Sessions.SelectedSession is { } s)
            for (int i = 0; i < s.ChannelCount; i++)
            {
                var option = new ExportChannelOption
                {
                    Index = i,
                    Name = i < s.ChannelNames.Length ? s.ChannelNames[i] : $"CH{i}",
                    Unit = i < s.ChannelUnits.Length ? s.ChannelUnits[i] : "",
                    IsSelected = keep == null || keep.GetValueOrDefault(i, true),
                };
                option.PropertyChanged += OnExportChannelChanged;
                ExportChannels.Add(option);
            }
        SyncExportIndices();
    }

    private void OnExportChannelChanged(object? sender, PropertyChangedEventArgs e) => SyncExportIndices();

    /// <summary>勾选结果写回导出参数：全选时留空（导出全部通道，包括会话之后新增的列）。</summary>
    private void SyncExportIndices()
    {
        var selected = ExportChannels.Where(c => c.IsSelected).Select(c => c.Index).ToArray();
        Sessions.ExportChannelIndices = selected.Length == ExportChannels.Count ? "" : string.Join(",", selected);
        ExportSessionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAllExportChannels()
    {
        var target = ExportChannels.Any(c => !c.IsSelected);
        foreach (var c in ExportChannels) c.IsSelected = target;
    }

    private bool CanExport() => HasSelection && !IsSelectedLegacy && ExportChannels.Any(c => c.IsSelected);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportSessionAsync() => Sessions.ExportSessionCommand.ExecuteAsync(Sessions.SelectedSession);

    private void OnConnectionChanged(IDeviceConnection? connection)
    {
        void Apply()
        {
            IsLiveConnected = connection != null;
            // 回放视图在连接建立时关闭，HistoryPlaybackViewModel 自身会停止发布显示流。
            if (connection != null) IsPlaybackOpen = false;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }

    public async Task OpenPlaybackAsync(string sessionId)
    {
        if (_dataBus.CurrentConnection != null) return;
        IsPlaybackOpen = true;
        await Playback.LoadSessionByIdAsync(sessionId);
    }

    private bool CanOpenSelectedPlayback() =>
        !IsLiveConnected && Sessions.SelectedSession is { } s && s.LegacyDataTable == null && s.Id != Sessions.ActiveSessionId;

    [RelayCommand(CanExecute = nameof(CanOpenSelectedPlayback))]
    private Task OpenSelectedPlaybackAsync() =>
        Sessions.SelectedSession is { } session ? OpenPlaybackAsync(session.Id) : Task.CompletedTask;

    [RelayCommand]
    private void BackToList()
    {
        if (Playback.StopCommand.CanExecute(null)) Playback.StopCommand.Execute(null);
        IsPlaybackOpen = false;
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span:mm\\:ss}" : span.ToString(@"mm\:ss");
}
