using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Infrastructure.Configuration;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 系统设置 ViewModel — 暴露 AppSettings 所有字段进行编辑
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigService _configService;

    public SettingsViewModel(IAppConfigService configService)
    {
        _configService = configService;
        _ = LoadSettingsAsync();
    }

    // ---- 连接设置 ----

    [ObservableProperty]
    private string _defaultPortName = "COM1";

    [ObservableProperty]
    private int _defaultBaudRate = 115200;

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    [ObservableProperty]
    private string _defaultIpAddress = "192.168.1.100";

    [ObservableProperty]
    private int _defaultPort = 5000;

    // ---- 存储设置 ----

    [ObservableProperty]
    private string _dataStoragePath = string.Empty;

    [ObservableProperty]
    private bool _autoSaveEnabled = true;

    // ---- 图表设置 ----

    [ObservableProperty]
    private int _chartRefreshRate = 30;

    public int[] RefreshRateOptions { get; } = [10, 15, 20, 30, 60];

    // ---- UI 设置 ----

    [ObservableProperty]
    private string _themeName = "Default";

    public string[] ThemeOptions { get; } = ["Default", "Dark", "Light"];

    // ---- 状态 ----

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    // ---- 命令 ----

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _configService.LoadSettingsAsync();

            DefaultPortName = settings.DefaultPortName ?? "COM1";
            DefaultBaudRate = settings.DefaultBaudRate;
            DefaultIpAddress = settings.DefaultIpAddress ?? "192.168.1.100";
            DefaultPort = settings.DefaultPort;
            DataStoragePath = settings.DataStoragePath;
            AutoSaveEnabled = settings.AutoSaveEnabled;
            ChartRefreshRate = settings.ChartRefreshRate;
            ThemeName = settings.ThemeName;

            StatusMessage = "设置已加载";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载设置失败: {ex.Message}";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new AppSettings
            {
                DefaultPortName = DefaultPortName,
                DefaultBaudRate = DefaultBaudRate,
                DefaultIpAddress = DefaultIpAddress,
                DefaultPort = DefaultPort,
                DataStoragePath = DataStoragePath,
                AutoSaveEnabled = AutoSaveEnabled,
                ChartRefreshRate = ChartRefreshRate,
                ThemeName = ThemeName,
            };

            await _configService.SaveSettingsAsync(settings);
            StatusMessage = "设置已保存（部分设置需重启生效）";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存设置失败: {ex.Message}";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        DefaultPortName = "COM1";
        DefaultBaudRate = 115200;
        DefaultIpAddress = "192.168.1.100";
        DefaultPort = 5000;
        DataStoragePath = string.Empty;
        AutoSaveEnabled = true;
        ChartRefreshRate = 30;
        ThemeName = "Default";
        StatusMessage = "已恢复默认值（需点击保存生效）";
        IsStatusError = false;
    }
}
