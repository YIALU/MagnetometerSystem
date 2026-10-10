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
/// 检查更新的应用层协调者：管理用户偏好（自动检查开关、上次检查时间、跳过的版本、
/// 最近发现的新版本）、启动及运行期间的静默检查、以及更新窗口的弹出。
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
    public const string KeyKnownUpdate = "update.knownUpdate";

    /// <summary>两次静默检查的最小间隔。</summary>
    internal static readonly TimeSpan SilentCheckInterval = TimeSpan.FromHours(24);

    /// <summary>启动后延迟多久才检查，避开数据库初始化和串口连接的高峰。</summary>
    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 运行期间多久看一次是否到了检查时间。真正联网仍受 24 小时间隔约束；
    /// 检查失败不记时间，所以断网时也按这个节奏重试。
    /// </summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private readonly IUpdateService _updateService;
    private readonly IUserPreferencesService _preferences;
    private readonly SemaphoreSlim _sourceGate = new(1, 1);

    /// <summary>
    /// 定时检查、手动检查和切换平台互斥执行，慢的旧结果不会覆盖新结果。
    /// 加锁顺序固定为先 _checkGate 后 _sourceGate。
    /// </summary>
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>每写入一次检查结果或切换一次平台加一；静默检查弹窗前据此确认自己仍是最新结果。</summary>
    private int _resultGeneration;
    private bool _sourceLoaded;
    private bool _dialogOpen;
    private readonly object _promptLock = new();
    private string? _promptedVersion;
    private DateTime _promptedAtUtc;

    public UpdateCoordinator(IUpdateService updateService, IUserPreferencesService preferences)
    {
        _updateService = updateService;
        _preferences = preferences;
    }

    public IUpdateService Service => _updateService;

    /// <summary>最近一次检查发现的新版本。供状态栏的"有新版本"按钮免去重新联网。</summary>
    public UpdateInfo? LastKnownUpdate { get; private set; }

    /// <summary>
    /// 之后的检查确认已是最新（例如发布被撤回或换了平台），以前记下的新版本作废。
    /// 订阅方据此撤下状态栏角标；在后台线程触发。
    /// </summary>
    public event Action? KnownUpdateCleared;

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
        // 等进行中的检查写完、且切换和清缓存做完之前不让新检查开始，
        // 旧平台的结果不会在清除之后写回，新平台的结果也不会被清掉。
        await _checkGate.WaitAsync();
        try
        {
            var changed = false;
            await _sourceGate.WaitAsync();
            try
            {
                var previous = _updateService.Options.PreferredSource;
                if (!_sourceLoaded)
                {
                    var saved = await _preferences.GetPreferenceAsync<string>(KeySource);
                    previous = Enum.TryParse<UpdateSource>(saved, out var parsed) && Enum.IsDefined(parsed) ? parsed : UpdateSource.Automatic;
                }
                changed = previous != source;
                _updateService.Options.PreferredSource = source;
                _sourceLoaded = true;
                await _preferences.SetPreferenceAsync(KeySource, source.ToString());
            }
            catch (Exception ex) { Log.Warning(ex, "保存更新平台失败"); }
            finally { _sourceGate.Release(); }

            if (changed)
            {
                Interlocked.Increment(ref _resultGeneration);
                await InvalidateKnownUpdateAsync();
            }
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>
    /// 换了更新平台后，以前记下的新版本和检查时间都不再作数：撤下角标，
    /// 下一轮定时检查按新平台重新联网。
    /// </summary>
    private async Task InvalidateKnownUpdateAsync()
    {
        var hadUpdate = LastKnownUpdate is not null;
        LastKnownUpdate = null;
        await TrySetKnownUpdateAsync(null);
        try { await _preferences.SetPreferenceAsync<DateTime?>(KeyLastCheckUtc, null); }
        catch (Exception ex) { Log.Warning(ex, "清除检查更新时间失败"); }
        if (hadUpdate) KnownUpdateCleared?.Invoke();
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
    /// 启动后的静默检查，之后在程序运行期间每到 24 小时再查一次。
    /// 常年不关的实例也能收到新版本提示；"稍后提醒"的版本次日会再提示一次。
    /// </summary>
    /// <param name="onUpdateFound">联网发现新版本时的回调（挂角标并弹窗），由调用方负责切到 UI 线程。</param>
    /// <param name="onUpdateRestored">未到检查时间、沿用上次发现的新版本时的回调（只挂角标）。</param>
    public async Task RunAutoCheckLoopAsync(
        Func<UpdateInfo, Task> onUpdateFound, Func<UpdateInfo, Task>? onUpdateRestored, CancellationToken ct = default)
    {
        await RunStartupCheckAsync(onUpdateFound, onUpdateRestored);
        while (true)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (!await IsAutoCheckEnabledAsync() || !await IsCheckDueAsync()) continue;
                await CheckSilentlyAsync(onUpdateFound);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "定时检查更新出错");
            }
        }
    }

    /// <summary>
    /// 启动后的静默检查。任何失败都只记日志，绝不弹窗——
    /// 这套软件常年跑在没有外网的工业现场，检查不到更新是常态而非异常。
    /// </summary>
    /// <param name="onUpdateFound">发现新版本时的回调，由调用方负责切到 UI 线程。</param>
    /// <param name="onUpdateRestored">未到检查时间、沿用上次发现的新版本时的回调。</param>
    public async Task RunStartupCheckAsync(Func<UpdateInfo, Task> onUpdateFound, Func<UpdateInfo, Task>? onUpdateRestored = null)
    {
        try
        {
            _updateService.CleanupDownloads();

            if (!await IsAutoCheckEnabledAsync())
            {
                Log.Information("自动检查更新已关闭，跳过");
                return;
            }

            if (!await IsCheckDueAsync())
            {
                // 24 小时内已查过：不联网，但把上次发现的版本挂回角标，
                // 否则当天重启后"稍后提醒"的版本就找不回来了。
                var known = await TryRestoreKnownUpdateAsync();
                if (known is not null && onUpdateRestored is not null) await onUpdateRestored(known);
                return;
            }

            await Task.Delay(StartupDelay);

            // 用户可能在启动延迟期间关闭了自动检查。
            if (!await IsAutoCheckEnabledAsync()) return;

            await CheckSilentlyAsync(onUpdateFound);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动时检查更新出错");
        }
    }

    private async Task CheckSilentlyAsync(Func<UpdateInfo, Task> onUpdateFound)
    {
        UpdateCheckResult result;
        int published;
        await _checkGate.WaitAsync();
        try
        {
            await GetSourceAsync();
            result = await _updateService.CheckForUpdateAsync();
            if (result.WarningMessage != null) Log.Warning("部分更新平台检查失败: {Message}", result.WarningMessage);

            if (result.Status == UpdateCheckStatus.Failed)
            {
                // 不记录本次检查时间，否则断网一次要等 24 小时才会再试
                Log.Warning("静默检查更新失败: {Message}", result.ErrorMessage);
                return;
            }

            await RecordResultAsync(result);
            published = Volatile.Read(ref _resultGeneration);
        }
        finally { _checkGate.Release(); }

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Info is null)
        {
            if (result.WarningMessage == null) Log.Information("当前已是最新版本 v{Version}", AppVersion.Number);
            else Log.Information("已成功检查的平台未发现新版本 v{Version}", AppVersion.Number);
            return;
        }

        var skipped = await TryGetSkippedVersionAsync();
        if (string.Equals(skipped, result.Info.Version, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("用户已选择跳过版本 v{Version}，不再提示", result.Info.Version);
            return;
        }

        if (!await IsAutoCheckEnabledAsync()) return;

        // 上面几步读偏好期间，用户可能换了平台或手动检查得到了更新的结果；
        // 这次结果已不是最新的，就不再挂角标、弹窗。
        if (published != Volatile.Read(ref _resultGeneration)) return;

        // 部分平台失败时不记检查时间，下一轮仍会联网重试；同一版本 24 小时内只弹一次，
        // 否则"稍后提醒"后每小时都会再弹。
        if (!TryMarkPrompted(result.Info.Version)) return;

        Log.Information("发现新版本 v{Version}", result.Info.Version);
        await onUpdateFound(result.Info);
    }

    /// <summary>同一版本 24 小时内已弹过窗（自动或手动）时返回 false；否则记下本次并返回 true。</summary>
    private bool TryMarkPrompted(string version)
    {
        lock (_promptLock)
        {
            var now = UtcNow();
            var sinceLast = now - _promptedAtUtc;
            if (string.Equals(_promptedVersion, version, StringComparison.OrdinalIgnoreCase)
                && sinceLast >= TimeSpan.Zero && sinceLast < SilentCheckInterval)
            {
                return false;
            }

            _promptedVersion = version;
            _promptedAtUtc = now;
            return true;
        }
    }

    private void MarkPrompted(string version)
    {
        lock (_promptLock)
        {
            _promptedVersion = version;
            _promptedAtUtc = UtcNow();
        }
    }

    private async Task<bool> IsCheckDueAsync()
    {
        var lastCheck = await TryGetLastCheckAsync();
        if (!lastCheck.HasValue) return true;
        var elapsed = UtcNow() - lastCheck.Value;
        // 系统时钟被往回调过时，上次检查时间会落在"未来"，按到期处理，免得长期不再检查。
        return elapsed < TimeSpan.Zero || elapsed >= SilentCheckInterval;
    }

    // ------------------------------------------------------------ 手动检查

    /// <summary>"关于"窗口里的手动检查。结果原样返回，由调用方决定怎么提示。</summary>
    public async Task<UpdateCheckResult> CheckManuallyAsync(CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            await GetSourceAsync();
            ct.ThrowIfCancellationRequested();
            var result = await _updateService.CheckForUpdateAsync(ct);
            if (result.Status != UpdateCheckStatus.Failed) await RecordResultAsync(result);
            return result;
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>
    /// 记录一次成功的检查。只有所有平台都答复时才写检查时间和"已是最新"；
    /// 部分平台失败时发现的新版本照样记下，但不清除以前记下的版本。
    /// 调用方须持有 _checkGate。
    /// </summary>
    private async Task RecordResultAsync(UpdateCheckResult result)
    {
        Interlocked.Increment(ref _resultGeneration);

        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Info is not null)
        {
            LastKnownUpdate = result.Info;
            await TrySetKnownUpdateAsync(result.Info);
        }
        else if (result.WarningMessage == null)
        {
            var hadUpdate = LastKnownUpdate is not null;
            LastKnownUpdate = null;
            await TrySetKnownUpdateAsync(null);
            if (hadUpdate) KnownUpdateCleared?.Invoke();
        }

        if (result.WarningMessage == null) await TrySetLastCheckAsync(UtcNow());
    }

    /// <summary>
    /// 弹出更新窗口，并在用户选择"跳过此版本"时落地该选择。必须在 UI 线程调用。
    /// </summary>
    public async Task ShowUpdateDialogAsync(Window? owner, UpdateInfo info)
    {
        // 定时检查可能恰好在用户从角标或"关于"打开更新窗口时触发，不叠第二个窗口。
        if (_dialogOpen) return;

        var dialog = new UpdateDialog(_updateService, info);

        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        // 用户从"关于"或角标看过的版本，定时检查 24 小时内也不再自动弹。
        MarkPrompted(info.Version);
        _dialogOpen = true;
        try { dialog.ShowDialog(); }
        finally { _dialogOpen = false; }

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

    /// <summary>
    /// 读回上次发现的新版本。已跳过、或已不比当前版本新（例如已经装好）的不再恢复。
    /// </summary>
    private async Task<UpdateInfo?> TryRestoreKnownUpdateAsync()
    {
        UpdateInfo? known;
        try { known = await _preferences.GetPreferenceAsync<UpdateInfo>(KeyKnownUpdate); }
        catch (Exception ex) { Log.Warning(ex, "读取上次发现的新版本失败"); return null; }
        if (known is null) return null;

        if (!IsNewerThanCurrent(known.Version))
        {
            await TrySetKnownUpdateAsync(null);
            return null;
        }

        LastKnownUpdate = known;
        var skipped = await TryGetSkippedVersionAsync();
        return string.Equals(skipped, known.Version, StringComparison.OrdinalIgnoreCase) ? null : known;
    }

    private bool IsNewerThanCurrent(string? version) =>
        TryParseVersion(version, out var candidate)
        && TryParseVersion(_updateService.Options.CurrentVersion, out var current)
        && candidate > current;

    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = raw?.Trim().TrimStart('v', 'V');
        return !string.IsNullOrEmpty(text) && Version.TryParse(text, out version!);
    }

    private async Task TrySetKnownUpdateAsync(UpdateInfo? info)
    {
        try { await _preferences.SetPreferenceAsync(KeyKnownUpdate, info); }
        catch (Exception ex) { Log.Warning(ex, "保存发现的新版本失败"); }
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
