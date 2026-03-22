using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    public ConnectionViewModel ConnectionVM { get; }
    public RealtimeChartViewModel RealtimeChartVM { get; }
    public SessionListViewModel SessionListVM { get; }
    public HistoryPlaybackViewModel HistoryPlaybackVM { get; }
    public OrthogonalityCalibrationViewModel OrthoCalibVM { get; }
    public SensorCalibrationViewModel SensorCalibVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel(ConnectionViewModel connectionVm, RealtimeChartViewModel realtimeChartVm, SessionListViewModel sessionListVm, HistoryPlaybackViewModel historyPlaybackVm, OrthogonalityCalibrationViewModel orthoCalibVm, SensorCalibrationViewModel sensorCalibVm, SettingsViewModel settingsVm)
    {
        ConnectionVM = connectionVm;
        RealtimeChartVM = realtimeChartVm;
        SessionListVM = sessionListVm;
        HistoryPlaybackVM = historyPlaybackVm;
        OrthoCalibVM = orthoCalibVm;
        SensorCalibVM = sensorCalibVm;
        SettingsVM = settingsVm;
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

        // 订阅实时图表的数据点计数
        RealtimeChartVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(RealtimeChartViewModel.DataPointCount))
            {
                DataCount = RealtimeChartVM.DataPointCount;
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
    }

    [RelayCommand]
    private void NavigateToConnection()
    {
        CurrentView = ConnectionVM;
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
    }

    [RelayCommand]
    private void NavigateToHistoryPlayback()
    {
        CurrentView = HistoryPlaybackVM;
    }

    [RelayCommand]
    private void NavigateToOrthogonalityCalibration()
    {
        CurrentView = OrthoCalibVM;
    }

    [RelayCommand]
    private void NavigateToSensorCalibration()
    {
        CurrentView = SensorCalibVM;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsVM;
    }
}
