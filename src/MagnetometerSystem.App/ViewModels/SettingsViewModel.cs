using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 系统设置 ViewModel。只放真正生效的设置：
/// 连接参数在“连接”页编辑并在退出时记住；采集后一律自动保存原始数据，因此没有开关。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigService _configService;
    private readonly UpdateCoordinator _updateCoordinator;

    public SettingsViewModel(IAppConfigService configService, UpdateCoordinator updateCoordinator, DatabaseInitializer? database = null)
    {
        _configService = configService;
        _updateCoordinator = updateCoordinator;
        DatabasePath = database?.DatabasePath ?? "";
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        await LoadSettingsAsync();
        _isLoaded = true;
    }

    // ---- 存储与日志（只读显示） ----

    /// <summary>采集数据所在的 SQLite 数据库文件。</summary>
    public string DatabasePath { get; }

    public string LogDirectory => GlobalErrorHandler.LogDirectory ?? "";

    [RelayCommand]
    private void OpenDatabaseFolder() => OpenFolder(Path.GetDirectoryName(DatabasePath));

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(LogDirectory);

    private void OpenFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            StatusMessage = "文件夹不存在";
            IsStatusError = true;
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = $"无法打开文件夹: {ex.Message}"; IsStatusError = true; }
    }

    // ---- 图表 ----

    /// <summary>绘图刷新率：只决定曲线多久重绘一次。修改后立即作用于曲线，并随设置保存。</summary>
    [ObservableProperty]
    private int _chartRefreshRate = 30;

    public int[] RefreshRateOptions { get; } = [10, 15, 20, 30, 60];

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
            if (settings.ChartRefreshRate > 0) ChartRefreshRate = settings.ChartRefreshRate;

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

            StatusMessage = "";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载设置失败");
            StatusMessage = "未能读取设置，请稍后重新加载。";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = await _configService.LoadSettingsAsync();
            settings.ChartRefreshRate = ChartRefreshRate;
            await _configService.SaveSettingsAsync(settings);
            StatusMessage = "已保存";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存设置失败");
            StatusMessage = "设置未能保存，请检查磁盘空间和保存位置后重试。";
            IsStatusError = true;
        }
    }

    partial void OnChartRefreshRateChanged(int value)
    {
        if (_isLoaded) _ = SaveSettingsAsync();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        ChartRefreshRate = 30;
        AutoCheckUpdateEnabled = true;   // 这一项即时保存
        UpdateSource = UpdateSource.Automatic;
        StatusMessage = "已恢复默认值";
        IsStatusError = false;
    }
}
