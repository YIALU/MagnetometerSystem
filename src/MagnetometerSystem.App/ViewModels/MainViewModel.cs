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

    public MainViewModel(ConnectionViewModel connectionVm, RealtimeChartViewModel realtimeChartVm)
    {
        ConnectionVM = connectionVm;
        RealtimeChartVM = realtimeChartVm;
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
}
