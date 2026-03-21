using CommunityToolkit.Mvvm.ComponentModel;

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

    public MainViewModel(ConnectionViewModel connectionVm)
    {
        ConnectionVM = connectionVm;
        CurrentView = connectionVm;

        // 订阅连接状态变化
        ConnectionVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
            {
                ConnectionStatus = ConnectionVM.IsConnected ? "已连接" : "未连接";
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
    }
}
