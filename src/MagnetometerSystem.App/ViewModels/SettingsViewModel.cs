using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 系统设置 ViewModel — 暴露 AppSettings 所有字段进行编辑
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigService _configService;
    private readonly UpdateCoordinator _updateCoordinator;

    public SettingsViewModel(IAppConfigService configService, UpdateCoordinator updateCoordinator)
    {
        _configService = configService;
        _updateCoordinator = updateCoordinator;
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await LoadSettingsAsync();
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

    // ---- 更新设置 ----

    /// <summary>
    /// 启动时自动检查更新。
    /// 这一项不走 AppSettings —— App.OnExit 会用主界面当前值整体覆盖 AppSettings，
    /// 放进去会被那次写回冲掉，所以单独存在 user_preferences 里，改动即时保存。
    /// </summary>
    [ObservableProperty]
    private bool _autoCheckUpdateEnabled = true;

    [ObservableProperty]
    private UpdateSource _updateSource = UpdateSource.Automatic;
    public IReadOnlyList<KeyValuePair<UpdateSource, string>> UpdateSources { get; } =
    [new(UpdateSource.Automatic, "自动（Gitee + GitHub）"), new(UpdateSource.Gitee, "Gitee"), new(UpdateSource.GitHub, "GitHub")];

    partial void OnUpdateSourceChanged(UpdateSource value)
    {
        if (!_suppressUpdatePreferenceWrite) _ = _updateCoordinator.SetSourceAsync(value);
    }

    /// <summary>加载阶段给属性赋值不应触发回写。</summary>
    private bool _suppressUpdatePreferenceWrite;

    partial void OnAutoCheckUpdateEnabledChanged(bool value)
    {
        if (_suppressUpdatePreferenceWrite) return;
        _ = _updateCoordinator.SetAutoCheckEnabledAsync(value);
    }

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

            _suppressUpdatePreferenceWrite = true;
            try
            {
                AutoCheckUpdateEnabled = await _updateCoordinator.IsAutoCheckEnabledAsync();
                UpdateSource = await _updateCoordinator.GetSourceAsync();
            }
            finally
            {
                _suppressUpdatePreferenceWrite = false;
            }

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
            var settings = await _configService.LoadSettingsAsync();
            settings.DefaultPortName = DefaultPortName;
            settings.DefaultBaudRate = DefaultBaudRate;
            settings.DefaultIpAddress = DefaultIpAddress;
            settings.DefaultPort = DefaultPort;
            settings.DataStoragePath = DataStoragePath;
            settings.AutoSaveEnabled = AutoSaveEnabled;
            settings.ChartRefreshRate = ChartRefreshRate;
            settings.ThemeName = ThemeName;

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
        AutoCheckUpdateEnabled = true;   // 这一项即时保存，不等"保存"按钮
        UpdateSource = UpdateSource.Automatic;
        StatusMessage = "已恢复默认值（需点击保存生效）";
        IsStatusError = false;
    }
}
