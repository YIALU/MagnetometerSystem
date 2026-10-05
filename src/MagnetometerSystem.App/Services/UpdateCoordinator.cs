using System.Windows;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Services;
using Serilog;

// Core 和 Infrastructure 各有一个同名的 IUserPreferencesService，直接写会歧义。
// App.OnStartup 注册的是 Infrastructure 那个，这里跟着它走。
using IUserPreferencesService = MagnetometerSystem.Infrastructure.Services.IUserPreferencesService;

namespace MagnetometerSystem.App.Services;

/// <summary>
/// 检查更新的应用层协调者：管理用户偏好（自动检查开关、上次检查时间、跳过的版本）、
/// 启动时的静默检查、以及更新窗口的弹出。
///
/// 偏好走 UserPreferencesService 而不是 AppSettings —— App.OnExit 会用主界面的当前值
/// 整体覆盖 AppSettings，把更新相关的设置放进去会被那次写回冲掉。
/// </summary>
public sealed class UpdateCoordinator
{
    public const string KeyAutoCheck = "update.autoCheckEnabled";
    public const string KeyLastCheckUtc = "update.lastCheckUtc";
    public const string KeySkippedVersion = "update.skippedVersion";
    public const string KeySource = "update.source";

    /// <summary>两次静默检查的最小间隔。</summary>
    private static readonly TimeSpan SilentCheckInterval = TimeSpan.FromHours(24);

    /// <summary>启动后延迟多久才检查，避开数据库初始化和串口连接的高峰。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    private readonly IUpdateService _updateService;
    private readonly IUserPreferencesService _preferences;
    private readonly SemaphoreSlim _sourceGate = new(1, 1);
    private bool _sourceLoaded;

    public UpdateCoordinator(IUpdateService updateService, IUserPreferencesService preferences)
    {
        _updateService = updateService;
        _preferences = preferences;
    }

    public IUpdateService Service => _updateService;

    /// <summary>最近一次检查发现的新版本。供状态栏的"有新版本"按钮免去重新联网。</summary>
    public UpdateInfo? LastKnownUpdate { get; private set; }

    // ------------------------------------------------------------------ 偏好

    public async Task<UpdateSource> GetSourceAsync()
    {
        await _sourceGate.WaitAsync();
        try
        {
            if (!_sourceLoaded)
            {
                var saved = await _preferences.GetPreferenceAsync<string>(KeySource);
                _updateService.Options.PreferredSource = Enum.TryParse<UpdateSource>(saved, out var source) && Enum.IsDefined(source)
                    ? source : UpdateSource.Automatic;
                _sourceLoaded = true;
            }
            return _updateService.Options.PreferredSource;
        }
        catch (Exception ex) { Log.Warning(ex, "读取更新平台失败，保留当前设置"); return _updateService.Options.PreferredSource; }
        finally { _sourceGate.Release(); }
    }

    public async Task SetSourceAsync(UpdateSource source)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        await _sourceGate.WaitAsync();
        try
        {
            _updateService.Options.PreferredSource = source;
            _sourceLoaded = true;
            await _preferences.SetPreferenceAsync(KeySource, source.ToString());
        }
        catch (Exception ex) { Log.Warning(ex, "保存更新平台失败"); }
        finally { _sourceGate.Release(); }
    }

    public async Task<bool> IsAutoCheckEnabledAsync()
    {
        try
        {
            return await _preferences.GetPreferenceAsync<bool?>(KeyAutoCheck) ?? true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取自动检查更新开关失败，按默认开启处理");
            return true;
        }
    }

    public async Task SetAutoCheckEnabledAsync(bool enabled)
    {
        try
        {
            await _preferences.SetPreferenceAsync(KeyAutoCheck, enabled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存自动检查更新开关失败");
        }
    }

    // ------------------------------------------------------------ 静默检查

    /// <summary>
    /// 启动后的静默检查。任何失败都只记日志，绝不弹窗——
    /// 这套软件常年跑在没有外网的工业现场，检查不到更新是常态而非异常。
    /// </summary>
    /// <param name="onUpdateFound">发现新版本时的回调，由调用方负责切到 UI 线程。</param>
    public async Task RunStartupCheckAsync(Func<UpdateInfo, Task> onUpdateFound)
    {
        try
        {
            _updateService.CleanupDownloads();

            if (!await IsAutoCheckEnabledAsync())
            {
                Log.Information("自动检查更新已关闭，跳过");
                return;
            }

            var lastCheck = await TryGetLastCheckAsync();
            if (lastCheck.HasValue && DateTime.UtcNow - lastCheck.Value < SilentCheckInterval)
            {
                return;
            }

            await Task.Delay(StartupDelay);

            await GetSourceAsync();
            var result = await _updateService.CheckForUpdateAsync();
            if (result.WarningMessage != null) Log.Warning("部分更新平台检查失败: {Message}", result.WarningMessage);

            if (result.Status == UpdateCheckStatus.Failed)
            {
                // 不记录本次检查时间，否则断网一次要等 24 小时才会再试
                Log.Warning("静默检查更新失败: {Message}", result.ErrorMessage);
                return;
            }

            if (result.WarningMessage == null) await TrySetLastCheckAsync(DateTime.UtcNow);

            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Info is null)
            {
                if (result.WarningMessage == null) Log.Information("当前已是最新版本 v{Version}", AppVersion.Number);
                else Log.Information("已成功检查的平台未发现新版本 v{Version}", AppVersion.Number);
                return;
            }

            LastKnownUpdate = result.Info;

            var skipped = await TryGetSkippedVersionAsync();
            if (string.Equals(skipped, result.Info.Version, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("用户已选择跳过版本 v{Version}，不再提示", result.Info.Version);
                return;
            }

            Log.Information("发现新版本 v{Version}", result.Info.Version);
            await onUpdateFound(result.Info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动时检查更新出错");
        }
    }

    // ------------------------------------------------------------ 手动检查

    /// <summary>"关于"窗口里的手动检查。结果原样返回，由调用方决定怎么提示。</summary>
    public async Task<UpdateCheckResult> CheckManuallyAsync(CancellationToken ct = default)
    {
        await GetSourceAsync();
        ct.ThrowIfCancellationRequested();
        var result = await _updateService.CheckForUpdateAsync(ct);

        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Info is not null)
        {
            LastKnownUpdate = result.Info;
            if (result.WarningMessage == null) await TrySetLastCheckAsync(DateTime.UtcNow);
        }
        else if (result.Status == UpdateCheckStatus.UpToDate)
        {
            if (result.WarningMessage == null) await TrySetLastCheckAsync(DateTime.UtcNow);
        }

        return result;
    }

    /// <summary>
    /// 弹出更新窗口，并在用户选择"跳过此版本"时落地该选择。必须在 UI 线程调用。
    /// </summary>
    public async Task ShowUpdateDialogAsync(Window? owner, UpdateInfo info)
    {
        var dialog = new UpdateDialog(_updateService, info);

        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();

        if (dialog.SkipRequested)
        {
            Log.Information("用户选择跳过版本 v{Version}", info.Version);
            await TrySetSkippedVersionAsync(info.Version);
        }
    }

    // ------------------------------------------------------------ 偏好读写

    private async Task<DateTime?> TryGetLastCheckAsync()
    {
        try { return await _preferences.GetPreferenceAsync<DateTime?>(KeyLastCheckUtc); }
        catch (Exception ex) { Log.Warning(ex, "读取上次检查更新时间失败"); return null; }
    }

    private async Task TrySetLastCheckAsync(DateTime utc)
    {
        try { await _preferences.SetPreferenceAsync(KeyLastCheckUtc, utc); }
        catch (Exception ex) { Log.Warning(ex, "保存检查更新时间失败"); }
    }

    private async Task<string?> TryGetSkippedVersionAsync()
    {
        try { return await _preferences.GetPreferenceAsync<string>(KeySkippedVersion); }
        catch (Exception ex) { Log.Warning(ex, "读取已跳过版本失败"); return null; }
    }

    private async Task TrySetSkippedVersionAsync(string version)
    {
        try { await _preferences.SetPreferenceAsync(KeySkippedVersion, version); }
        catch (Exception ex) { Log.Warning(ex, "保存已跳过版本失败"); }
    }
}
