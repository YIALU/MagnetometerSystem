using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>导航栏上的页面。</summary>
public enum AppPage { Workspace, Connection, Commands, Data, Analysis, Calibration, Settings }

/// <summary>链路条左侧的连接状态。</summary>
public enum LinkState { Idle, Connecting, Acquiring, Interrupted, Stopping }

/// <summary>事件记录的一行：采集生命周期、首帧、保存结果与错误。</summary>
public sealed record WorkspaceEvent(DateTime Time, string Level, string Text)
{
    public bool IsError => Level == "error";
    public bool IsWarning => Level == "warn";
}

/// <summary>“校正”页：正交度、偏移/增益与配置库放在同一入口。</summary>
public sealed partial class CalibrationPage(OrthogonalityCalibrationViewModel ortho, SensorCalibrationViewModel sensor) : ObservableObject
{
    public const int OrthoTab = 0, SensorTab = 1, LibraryTab = 2;

    public OrthogonalityCalibrationViewModel Ortho { get; } = ortho;
    public SensorCalibrationViewModel Sensor { get; } = sensor;

    [ObservableProperty] private int _selectedTab;
}

/// <summary>
/// 主窗口 ViewModel：页面导航与“数据链路”条（接收 → 解析 → 保存）。
/// 三段计数来自各自来源，不混用：接收字节、解析帧数、已提交 / 待写读数。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private AppPage _currentPage = AppPage.Workspace;

    [ObservableProperty]
    private string _connectionStatus = "未连接";

    [ObservableProperty]
    private string _sensorInfo = "无";

    [ObservableProperty]
    private string _sampleRateInfo = "—";

    [ObservableProperty]
    private long _dataCount;

    [ObservableProperty]
    private string _activeSessionName = "";

    /// <summary>
    /// 后台初始化（数据库迁移 + 配置加载）是否已完成。
    /// 初始为 false，完成后置为 true，用于驱动加载遮罩的可见性。
    /// </summary>
    [ObservableProperty]
    private bool _isInitialized = false;

    /// <summary>
    /// 检查更新发现的可用新版本号（如 "0.4.0"），无更新时为 null。
    /// 驱动导航栏底部版本号旁的提示点。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string? _availableUpdateVersion;

    /// <summary>是否有可用更新。</summary>
    public bool HasUpdate => !string.IsNullOrEmpty(AvailableUpdateVersion);

    // ---- 数据链路条 ----
    [ObservableProperty] private LinkState _linkState = LinkState.Idle;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _receivedBytesText = "—";
    [ObservableProperty] private string _receivedBytesUnit = "";
    [ObservableProperty] private string _receiveRateText = "— kB/s";
    [ObservableProperty] private string _measuredRateText = "—";

    public string LinkTitle => LinkState switch
    {
        LinkState.Connecting => "正在连接",
        LinkState.Acquiring => "采集中",
        LinkState.Interrupted => "连接中断",
        LinkState.Stopping => "正在停止",
        _ => "未连接",
    };

    public bool IsLinkActive => LinkState != LinkState.Idle;

    /// <summary>采集页空状态：未连接且曲线没有数据。断开后保留刚采到的曲线，不被遮住。</summary>
    public bool ShowWorkspaceEmptyState => !IsLinkActive && RealtimeChartVM.DataPointCount == 0;

    public string PortSummary => ConnectionVM.SelectedConnectionType == ConnectionType.Tcp
        ? $"TCP {ConnectionVM.IpAddress}:{ConnectionVM.Port}"
        : $"{ConnectionVM.SelectedPort}   {ConnectionVM.BaudRate} {ConnectionVM.DataBits}{ParityLetter(ConnectionVM.Parity)}{ConnectionVM.StopBits:0.#}";

    public string ProtocolInfo => ConnectionVM.ProtocolConfig?.Name ?? "未选择协议";

    public string ProtocolSummary => ConnectionVM.ProtocolConfig is { } p
        ? $"{p.Name}（{(p.Category == ProtocolCategory.Binary ? "二进制" : "ASCII")}）"
        : "未选择协议";

    public string NominalRateText => $"{ConnectionVM.SampleRate:0.###} Hz";

    public bool HasStorageError => !string.IsNullOrEmpty(SessionListVM.StorageError);

    /// <summary>保存段状态：只有存储服务报告的结果才显示为“正常 / 失败”。</summary>
    public string SaveStateText => HasStorageError ? "保存失败"
        : SessionListVM.IsRecording ? (SessionListVM.PendingReadingCount > 0 ? "写入中" : "正常")
        : IsLinkActive ? "准备中" : "就绪";

    public string SaveSubText => SessionListVM.IsRecording || HasStorageError
        ? (string.IsNullOrEmpty(ActiveSessionName) ? "当前会话" : ActiveSessionName)
        : "连接后创建新会话";

    public string ToggleAcquisitionText => LinkState switch
    {
        LinkState.Connecting => "正在连接…",
        LinkState.Stopping => "正在保存尾批…",
        LinkState.Idle => "连接并开始保存",
        _ => "停止并断开",
    };

    public ConnectionViewModel ConnectionVM { get; }
    public RealtimeChartViewModel RealtimeChartVM { get; }
    public SessionListViewModel SessionListVM { get; }
    public HistoryPlaybackViewModel HistoryPlaybackVM { get; }
    public OrthogonalityCalibrationViewModel OrthoCalibVM { get; }
    public SensorCalibrationViewModel SensorCalibVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public DeviceCommandViewModel DeviceCommandVM { get; }
    public DataPageViewModel DataPage { get; }
    public AnalysisViewModel? AnalysisVM { get; }
    public CalibrationPage Calibration { get; }
    public WorkspaceLayoutViewModel WorkspaceLayout => RealtimeChartVM.WorkspaceLayout;

    /// <summary>暴露 DataBus 给链路条触发"记录当前点"</summary>
    public DataBus DataBus { get; }

    /// <summary>手动正交度采集状态（链路条上的采集提示绑定）</summary>
    public ManualOrthoState ManualOrthoState => DataBus.ManualOrthoState;

    private const int MaxEvents = 300;

    /// <summary>底部停靠区“事件”页：只记录已从状态得到证据的事情。</summary>
    public ObservableCollection<WorkspaceEvent> Events { get; } = new();

    private DispatcherTimer? _linkTimer;
    private DateTime _acquisitionStartedAt;
    private long _lastBytes, _lastParsed;
    private DateTime _lastSample;

    public MainViewModel(ConnectionViewModel connectionVm, RealtimeChartViewModel realtimeChartVm, SessionListViewModel sessionListVm, HistoryPlaybackViewModel historyPlaybackVm, OrthogonalityCalibrationViewModel orthoCalibVm, SensorCalibrationViewModel sensorCalibVm, SettingsViewModel settingsVm, DeviceCommandViewModel deviceCommandVm, DataBus dataBus, AnalysisViewModel? analysisVm = null)
    {
        ConnectionVM = connectionVm;
        RealtimeChartVM = realtimeChartVm;
        SessionListVM = sessionListVm;
        HistoryPlaybackVM = historyPlaybackVm;
        OrthoCalibVM = orthoCalibVm;
        SensorCalibVM = sensorCalibVm;
        SettingsVM = settingsVm;
        DeviceCommandVM = deviceCommandVm;
        AnalysisVM = analysisVm;
        DataBus = dataBus;
        DataPage = new DataPageViewModel(sessionListVm, historyPlaybackVm, dataBus);
        Calibration = new CalibrationPage(orthoCalibVm, sensorCalibVm);
        CurrentView = this;

        ConnectionVM.PropertyChanged += OnConnectionPropertyChanged;

        // 初始协议的内置命令（构造时 ProtocolConfig 已有默认值，不会触发上面的变更事件）
        DeviceCommandVM.SetProtocolCommands(ConnectionVM.ProtocolConfig?.Commands);

        SessionListVM.PropertyChanged += OnSessionPropertyChanged;

        RealtimeChartVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RealtimeChartViewModel.DataPointCount))
                OnPropertyChanged(nameof(ShowWorkspaceEmptyState));
        };

        // 设置页的绘图刷新率立即作用于曲线；退出时由曲线的当前值写回设置。
        SettingsVM.ChartRefreshRate = RealtimeChartVM.RefreshRate;
        SettingsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ChartRefreshRate) && SettingsVM.ChartRefreshRate > 0)
                RealtimeChartVM.RefreshRate = SettingsVM.ChartRefreshRate;
        };

        // 会话列表的回放请求：留在“数据”页，切换到回放视图并加载会话
        SessionListVM.PlaybackRequested += async sessionId =>
        {
            CurrentView = DataPage;
            await DataPage.OpenPlaybackAsync(sessionId);
        };
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConnectionViewModel.IsConnected):
                ConnectionStatus = ConnectionVM.IsConnected ? "已连接" : "未连接";
                // 连接成功后自动切换到采集页
                if (ConnectionVM.IsConnected) CurrentView = this;
                UpdateLinkState();
                break;
            case nameof(ConnectionViewModel.IsAcquiring):
            case nameof(ConnectionViewModel.IsConnecting):
                UpdateLinkState();
                break;
            case nameof(ConnectionViewModel.SampleRate):
                SampleRateInfo = $"{ConnectionVM.SampleRate} Hz";
                OnPropertyChanged(nameof(NominalRateText));
                break;
            case nameof(ConnectionViewModel.ProtocolConfig):
                // 协议自带的命令组随协议切换：设备命令页只展示当前协议的指令
                DeviceCommandVM.SetProtocolCommands(ConnectionVM.ProtocolConfig?.Commands);
                OnPropertyChanged(nameof(ProtocolInfo));
                OnPropertyChanged(nameof(ProtocolSummary));
                break;
            case nameof(ConnectionViewModel.ReceivedByteCount):
                (ReceivedBytesText, ReceivedBytesUnit) = FormatBytes(ConnectionVM.ReceivedByteCount);
                break;
            case nameof(ConnectionViewModel.ParsedReadingCount):
                if (ConnectionVM.ParsedReadingCount > 0 && !_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AddEvent("info", "收到首帧，开始解析");
                }
                break;
            case nameof(ConnectionViewModel.LastError):
                if (!string.IsNullOrEmpty(ConnectionVM.LastError)) AddEvent("error", ConnectionVM.LastError);
                break;
            case nameof(ConnectionViewModel.SelectedConnectionType):
            case nameof(ConnectionViewModel.SelectedPort):
            case nameof(ConnectionViewModel.BaudRate):
            case nameof(ConnectionViewModel.DataBits):
            case nameof(ConnectionViewModel.Parity):
            case nameof(ConnectionViewModel.StopBits):
            case nameof(ConnectionViewModel.IpAddress):
            case nameof(ConnectionViewModel.Port):
                OnPropertyChanged(nameof(PortSummary));
                break;
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionListViewModel.ActiveSessionReadingCount):
                DataCount = SessionListVM.ActiveSessionReadingCount;
                break;
            case nameof(SessionListViewModel.ActiveSessionId):
                var activeId = SessionListVM.ActiveSessionId;
                ActiveSessionName = activeId == null ? ""
                    : SessionListVM.Sessions.FirstOrDefault(s => s.Id == activeId)?.Name ?? "";
                OnPropertyChanged(nameof(SaveSubText));
                if (activeId != null) AddEvent("info", $"已创建会话 {(ActiveSessionName.Length > 0 ? ActiveSessionName : activeId[..Math.Min(8, activeId.Length)])}");
                else if (_lastSessionId != null) AddEvent("info", "会话已结束，尾批已提交");
                _lastSessionId = activeId;
                break;
            case nameof(SessionListViewModel.StorageError):
                if (HasStorageError) AddEvent("error", "保存失败：" + SessionListVM.StorageError);
                else if (_hadStorageError) AddEvent("info", "保存已恢复");
                _hadStorageError = HasStorageError;
                OnPropertyChanged(nameof(HasStorageError));
                OnPropertyChanged(nameof(SaveStateText));
                OnPropertyChanged(nameof(SaveSubText));
                break;
            case nameof(SessionListViewModel.IsRecording):
            case nameof(SessionListViewModel.PendingReadingCount):
                OnPropertyChanged(nameof(SaveStateText));
                OnPropertyChanged(nameof(SaveSubText));
                break;
        }
    }

    partial void OnActiveSessionNameChanged(string value) => OnPropertyChanged(nameof(SaveSubText));

    private void UpdateLinkState()
    {
        var vm = ConnectionVM;
        // IsConnecting 同时覆盖“正在连接”和“正在断开”，按之前的状态区分。
        var busy = LinkState is LinkState.Acquiring or LinkState.Interrupted or LinkState.Stopping
            ? LinkState.Stopping : LinkState.Connecting;
        var state = vm.IsConnecting && (busy == LinkState.Connecting ? !vm.IsConnected : vm.IsAcquiring) ? busy
            : vm.IsAcquiring && vm.IsConnected ? LinkState.Acquiring
            : vm.IsAcquiring ? LinkState.Interrupted
            : LinkState.Idle;
        if (state == LinkState) return;
        var wasIdle = LinkState == LinkState.Idle;
        LinkState = state;
        if (wasIdle && state != LinkState.Idle) { _firstFrameLogged = false; StartLinkTimer(); }
        else if (state == LinkState.Idle) StopLinkTimer();
        switch (state)
        {
            case LinkState.Connecting: AddEvent("info", $"正在连接 {PortSummary}"); break;
            case LinkState.Acquiring: AddEvent("info", $"已连接 {PortSummary}，原始数据自动保存"); break;
            case LinkState.Interrupted: AddEvent("warn", "连接中断，会话保持打开；停止后结束会话"); break;
            case LinkState.Stopping: AddEvent("info", "正在停止并保存尾批"); break;
            case LinkState.Idle: AddEvent("info", "已断开"); break;
        }
    }

    partial void OnLinkStateChanged(LinkState value)
    {
        OnPropertyChanged(nameof(LinkTitle));
        OnPropertyChanged(nameof(IsLinkActive));
        OnPropertyChanged(nameof(ShowWorkspaceEmptyState));
        OnPropertyChanged(nameof(ToggleAcquisitionText));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(SaveSubText));
    }

    private void StartLinkTimer()
    {
        _acquisitionStartedAt = _lastSample = DateTime.Now;
        _lastBytes = _lastParsed = 0;
        ElapsedText = "00:00:00";
        _linkTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleLinkRates(), Dispatcher.CurrentDispatcher);
        _linkTimer.Start();
    }

    private void StopLinkTimer()
    {
        _linkTimer?.Stop();
        ElapsedText = "";
        ReceiveRateText = "— kB/s";
        MeasuredRateText = "—";
    }

    /// <summary>实测频率 = 每秒新增解析帧数；不使用标称采样率推算。</summary>
    private void SampleLinkRates()
    {
        var now = DateTime.Now;
        var seconds = (now - _lastSample).TotalSeconds;
        if (seconds <= 0) return;
        var bytes = ConnectionVM.ReceivedByteCount;
        var parsed = ConnectionVM.ParsedReadingCount;
        // 计数在新连接时归零，出现回退时重新取基线。
        if (bytes < _lastBytes || parsed < _lastParsed) { _lastBytes = bytes; _lastParsed = parsed; }
        ReceiveRateText = $"{(bytes - _lastBytes) / seconds / 1024:0.0} kB/s";
        MeasuredRateText = LinkState == LinkState.Acquiring ? $"{(parsed - _lastParsed) / seconds:0.0} Hz" : "—";
        _lastBytes = bytes; _lastParsed = parsed; _lastSample = now;
        ElapsedText = (now - _acquisitionStartedAt).ToString(@"hh\:mm\:ss");
    }

    private bool _firstFrameLogged, _hadStorageError;
    private string? _lastSessionId;

    private void AddEvent(string level, string text)
    {
        void Add()
        {
            Events.Insert(0, new WorkspaceEvent(DateTime.Now, level, text));
            while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Add();
        else dispatcher.BeginInvoke(Add);
    }

    [RelayCommand]
    private void ClearEvents() => Events.Clear();

    private static (string Value, string Unit) FormatBytes(long bytes) => bytes switch
    {
        < 1024 => (bytes.ToString("N0"), "B"),
        < 1024 * 1024 => ((bytes / 1024.0).ToString("0.0"), "KB"),
        < 1024L * 1024 * 1024 => ((bytes / 1024.0 / 1024).ToString("0.00"), "MB"),
        _ => ((bytes / 1024.0 / 1024 / 1024).ToString("0.00"), "GB"),
    };

    private static string ParityLetter(string parity) => parity switch
    {
        "Odd" => "O",
        "Even" => "E",
        "Mark" => "M",
        "Space" => "S",
        _ => "N",
    };

    // ---- 导航 ----

    partial void OnCurrentViewChanged(object? value)
    {
        // 旧的独立页面已合并：会话 / 回放进“数据”，正交度 / 偏移增益进“校正”。
        switch (value)
        {
            case SessionListViewModel:
                CurrentView = DataPage;
                return;
            case HistoryPlaybackViewModel:
                CurrentView = DataPage;
                if (DataBus.CurrentConnection == null) DataPage.IsPlaybackOpen = true;
                return;
            case OrthogonalityCalibrationViewModel:
                Calibration.SelectedTab = CalibrationPage.OrthoTab;
                CurrentView = Calibration;
                return;
            case SensorCalibrationViewModel:
                Calibration.SelectedTab = CalibrationPage.SensorTab;
                CurrentView = Calibration;
                return;
        }
        var page = value switch
        {
            ConnectionViewModel => AppPage.Connection,
            DeviceCommandViewModel => AppPage.Commands,
            DataPageViewModel => AppPage.Data,
            AnalysisViewModel => AppPage.Analysis,
            CalibrationPage => AppPage.Calibration,
            SettingsViewModel => AppPage.Settings,
            _ => AppPage.Workspace,
        };
        if (page != CurrentPage) CurrentPage = page;
    }

    partial void OnCurrentPageChanged(AppPage value) => NavigateTo(value);

    private void NavigateTo(AppPage page)
    {
        switch (page)
        {
            case AppPage.Workspace:
                if (CurrentView != this) CurrentView = this;
                break;
            case AppPage.Connection:
                if (CurrentView != ConnectionVM) CurrentView = ConnectionVM;
                _ = ConnectionVM.EnsureLoadedAsync();
                break;
            case AppPage.Commands:
                if (CurrentView != DeviceCommandVM) CurrentView = DeviceCommandVM;
                break;
            case AppPage.Data:
                if (CurrentView != DataPage) CurrentView = DataPage;
                _ = SessionListVM.EnsureLoadedAsync();
                break;
            case AppPage.Analysis:
                if (AnalysisVM is null) { CurrentPage = AppPage.Workspace; return; }
                if (CurrentView != AnalysisVM) CurrentView = AnalysisVM;
                _ = AnalysisVM.EnsureLoadedAsync();
                break;
            case AppPage.Calibration:
                if (CurrentView != Calibration) CurrentView = Calibration;
                _ = OrthoCalibVM.EnsureLoadedAsync();
                _ = SensorCalibVM.EnsureLoadedAsync();
                break;
            case AppPage.Settings:
                if (CurrentView != SettingsVM) CurrentView = SettingsVM;
                _ = SettingsVM.EnsureLoadedAsync();
                break;
        }
    }

    [RelayCommand] private void NavigateToConnection() => CurrentPage = AppPage.Connection;
    [RelayCommand] private void NavigateToRealtimeChart() => CurrentPage = AppPage.Workspace;
    [RelayCommand] private void NavigateToSessionList() => CurrentPage = AppPage.Data;
    [RelayCommand] private void NavigateToHistoryPlayback() => CurrentPage = AppPage.Data;
    [RelayCommand]
    private void NavigateToOrthogonalityCalibration()
    {
        Calibration.SelectedTab = CalibrationPage.OrthoTab;
        CurrentPage = AppPage.Calibration;
    }

    [RelayCommand]
    private void NavigateToSensorCalibration()
    {
        Calibration.SelectedTab = CalibrationPage.SensorTab;
        CurrentPage = AppPage.Calibration;
    }
    [RelayCommand] private void NavigateToSettings() => CurrentPage = AppPage.Settings;
    [RelayCommand] private void NavigateToDeviceCommand() => CurrentPage = AppPage.Commands;
    [RelayCommand] private void NavigateToAnalysis() => CurrentPage = AppPage.Analysis;

    // ---- 链路条操作 ----

    /// <summary>“异常 N”：回到采集页并打开底部原始报文，查看解析失败与重新同步。</summary>
    [RelayCommand]
    private void ShowRawFrames()
    {
        CurrentPage = AppPage.Workspace;
        WorkspaceLayout.ShowDock(WorkspaceLayoutViewModel.DockRawFrames);
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        var dir = GlobalErrorHandler.LogDirectory;
        if (string.IsNullOrEmpty(dir)) return;
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception ex) { Trace.TraceWarning($"打开日志目录失败: {ex.Message}"); }
    }

    [RelayCommand]
    private void RecordOrthoPoint() => DataBus.RaiseManualOrthoRecord();
}
