using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

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
    /// 初始为 false，完成后置为 true，用于驱动加载遮���的可见性。
    /// </summary>
    [ObservableProperty]
    private bool _isInitialized = false;

    public ConnectionViewModel ConnectionVM { get; }
    public RealtimeChartViewModel RealtimeChartVM { get; }
    public SessionListViewModel SessionListVM { get; }
    public HistoryPlaybackViewModel HistoryPlaybackVM { get; }
    public OrthogonalityCalibrationViewModel OrthoCalibVM { get; }
    public SensorCalibrationViewModel SensorCalibVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public DeviceCommandViewModel DeviceCommandVM { get; }

    /// <summary>暴露 DataBus 给左侧导航栏触发"记录当前点"</summary>
    public DataBus DataBus { get; }

    /// <summary>手动正交度采集状态（左侧蓝色卡片绑定）</summary>
    public ManualOrthoState ManualOrthoState => DataBus.ManualOrthoState;

    public MainViewModel(ConnectionViewModel connectionVm, RealtimeChartViewModel realtimeChartVm, SessionListViewModel sessionListVm, HistoryPlaybackViewModel historyPlaybackVm, OrthogonalityCalibrationViewModel orthoCalibVm, SensorCalibrationViewModel sensorCalibVm, SettingsViewModel settingsVm, DeviceCommandViewModel deviceCommandVm, DataBus dataBus)
    {
        ConnectionVM = connectionVm;
        RealtimeChartVM = realtimeChartVm;
        SessionListVM = sessionListVm;
        HistoryPlaybackVM = historyPlaybackVm;
        OrthoCalibVM = orthoCalibVm;
        SensorCalibVM = sensorCalibVm;
        SettingsVM = settingsVm;
        DeviceCommandVM = deviceCommandVm;
        DataBus = dataBus;
        CurrentView = connectionVm;

        // 订阅连接状态变化
        ConnectionVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
            {
                ConnectionStatus = ConnectionVM.IsConnected ? "已连接" : "未连接";

                // 连接成功后自动切换到实时采集页面
                if (ConnectionVM.IsConnected)
                {
                    CurrentView = RealtimeChartVM;
                }
            }
            else if (e.PropertyName == nameof(ConnectionViewModel.SelectedSensorType))
            {
                SensorInfo = ConnectionVM.SelectedSensorType.ToString();
            }
            else if (e.PropertyName == nameof(ConnectionViewModel.SampleRate))
            {
                SampleRateInfo = $"{ConnectionVM.SampleRate} Hz";
            }
        };

        // 订阅会话列表的"实时录制点数"，作为左侧卡片 / 状态栏的可靠数据源
        // （比 RealtimeChartVM.DataPointCount 可靠：只在图表页可见时才更新）
        SessionListVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SessionListViewModel.ActiveSessionReadingCount))
            {
                DataCount = SessionListVM.ActiveSessionReadingCount;
            }
        };

        // 订阅会话列表的回放请求，自动导航到回放页面并加载会话
        SessionListVM.PlaybackRequested += sessionId =>
        {
            CurrentView = HistoryPlaybackVM;

            // 在可用会话列表中选中对应会话，然后触发加载
            var target = HistoryPlaybackVM.AvailableSessions.FirstOrDefault(s => s.Id == sessionId);
            if (target != null)
            {
                HistoryPlaybackVM.SelectedSession = target;
                HistoryPlaybackVM.LoadSessionCommand.Execute(null);
            }
        };

        // 订阅会话列表的活跃会话变化，更新录制状态卡片显示名称
        SessionListVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SessionListViewModel.ActiveSessionId))
            {
                var activeId = SessionListVM.ActiveSessionId;
                if (activeId == null)
                {
                    ActiveSessionName = "";
                }
                else
                {
                    var session = SessionListVM.Sessions.FirstOrDefault(sess => sess.Id == activeId);
                    ActiveSessionName = session?.Name ?? "";
                }
            }
        };
    }

    [RelayCommand]
    private void NavigateToConnection()
    {
        CurrentView = ConnectionVM;
        _ = ConnectionVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToRealtimeChart()
    {
        CurrentView = RealtimeChartVM;
    }

    [RelayCommand]
    private void NavigateToSessionList()
    {
        CurrentView = SessionListVM;
        _ = SessionListVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToHistoryPlayback()
    {
        CurrentView = HistoryPlaybackVM;
        _ = HistoryPlaybackVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToOrthogonalityCalibration()
    {
        CurrentView = OrthoCalibVM;
        _ = OrthoCalibVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSensorCalibration()
    {
        CurrentView = SensorCalibVM;
        _ = SensorCalibVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsVM;
        _ = SettingsVM.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToDeviceCommand()
    {
        CurrentView = DeviceCommandVM;
    }
}
