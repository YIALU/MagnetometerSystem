using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using IUserPreferencesService = MagnetometerSystem.Infrastructure.Services.IUserPreferencesService;

namespace MagnetometerSystem.App.Tests;

public class UpdateStartupTests
{
    [Theory]
    [InlineData("latest")]
    [InlineData("failed")]
    [InlineData("exception")]
    [InlineData("skipped")]
    public async Task StartupSuppressesUnwantedPromptsAndNeverDownloads(string scenario)
    {
        var preferences = new Preferences();
        var service = new RecordingService
        {
            Result = scenario == "latest" ? UpdateCheckResult.UpToDate()
                : scenario == "failed" ? UpdateCheckResult.Failed("private detail") : UpdateCheckResult.Available(Info()),
            ThrowOnCheck = scenario == "exception"
        };
        if (scenario == "skipped") await preferences.SetPreferenceAsync(UpdateCoordinator.KeySkippedVersion, "2.0.0");
        var coordinator = new UpdateCoordinator(service, preferences);
        var prompts = 0;
        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        Assert.Equal(1, service.CheckCalls); Assert.Equal(0, prompts);
        Assert.Equal(0, service.DownloadCalls); Assert.Equal(0, service.ApplyCalls);
        var checkedAt = await preferences.GetPreferenceAsync<DateTime?>(UpdateCoordinator.KeyLastCheckUtc);
        Assert.Equal(scenario is "latest" or "skipped", checkedAt.HasValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualCheckWorksWhenStartupDisabledOrCheckedRecently(bool disabled)
    {
        var preferences = new Preferences();
        if (disabled) await preferences.SetPreferenceAsync(UpdateCoordinator.KeyAutoCheck, false);
        else await preferences.SetPreferenceAsync(UpdateCoordinator.KeyLastCheckUtc, DateTime.UtcNow);
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeySkippedVersion, "2.0.0");
        var service = new RecordingService();
        var coordinator = new UpdateCoordinator(service, preferences);
        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"));
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await coordinator.CheckManuallyAsync()).Status);
        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(0, service.DownloadCalls); Assert.Equal(0, service.ApplyCalls);
    }

    [Fact]
    public async Task DisablingDuringStartupDelayPreventsNetworkAndPrompt()
    {
        var service = new RecordingService();
        var coordinator = new UpdateCoordinator(service, new Preferences());
        var pending = coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"));
        await coordinator.SetAutoCheckEnabledAsync(false);
        await pending;
        Assert.Equal(0, service.CheckCalls); Assert.Equal(0, service.DownloadCalls);
    }

    [Fact]
    public async Task DisablingWhileRequestRunsSuppressesPrompt()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingService { PendingResponse = response.Task };
        var coordinator = new UpdateCoordinator(service, new Preferences());
        var prompts = 0;
        var pending = coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        try
        {
            await service.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await coordinator.SetAutoCheckEnabledAsync(false);
        }
        finally { response.TrySetResult(UpdateCheckResult.Available(Info())); }
        await pending;
        Assert.Equal(0, prompts); Assert.Equal(0, service.DownloadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AvailableVersionShowsDialogAndChoiceNeverDownloads(bool skip) => WpfTestHost.RunAsync(async () =>
    {
        var service = new RecordingService();
        var preferences = new Preferences();
        var coordinator = new UpdateCoordinator(service, preferences);
        var prompts = 0;
        Exception? interactionError = null;
        await coordinator.RunStartupCheckAsync(async info =>
        {
            prompts++;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<UpdateDialog>().Single();
                try
                {
                    Assert.Equal(0, service.DownloadCalls); Assert.Equal(0, service.ApplyCalls);
                    dialog.Left = -10000; dialog.Top = -10000;
                    UpdateSourceUiTests.SaveScreenshot((FrameworkElement)dialog.Content, "startup-update-prompt.png");
                    ((Button)dialog.FindName(skip ? "SkipButton" : "LaterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { interactionError = ex; }
                finally { dialog.Close(); timer.Stop(); }
            };
            try { timer.Start(); await coordinator.ShowUpdateDialogAsync(null, info); }
            finally { timer.Stop(); }
        });
        Assert.Equal(1, prompts); Assert.Equal("2.0.0", coordinator.LastKnownUpdate!.Version);
        Assert.Equal(0, service.DownloadCalls); Assert.Equal(0, service.ApplyCalls);
        Assert.Null(interactionError);
        Assert.Equal(skip ? "2.0.0" : null, await preferences.GetPreferenceAsync<string>(UpdateCoordinator.KeySkippedVersion));
    });

    [Fact]
    public async Task SameDayRestartRestoresBadgeFromSavedVersionWithoutNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "update-known-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var db = new DatabaseInitializer(Path.Combine(directory, "test.db")); await db.InitializeAsync();
            var found = Info() with { Mirrors = [Info() with { Source = UpdateSource.GitHub, DownloadUrl = "https://example.invalid/gh.exe" }] };
            var first = new UpdateCoordinator(new RecordingService { Result = UpdateCheckResult.Available(found) }, new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db))
            { StartupDelay = TimeSpan.Zero };
            var prompts = 0;
            await first.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
            Assert.Equal(1, prompts); // 用户点"稍后提醒"关掉，随后当天重启。

            var service = new RecordingService();
            var restarted = new UpdateCoordinator(service, new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db));
            UpdateInfo? restored = null;
            await restarted.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"),
                info => { restored = info; return Task.CompletedTask; });

            Assert.Equal(0, service.CheckCalls);
            Assert.NotNull(restored);
            Assert.Equal("2.0.0", restored!.Version);
            Assert.Equal(found.DownloadUrl, restored.DownloadUrl);
            Assert.Equal(found.ReleaseNotes, restored.ReleaseNotes);
            Assert.Equal("https://example.invalid/gh.exe", Assert.Single(restored.Mirrors).DownloadUrl);
            Assert.Same(restored, restarted.LastKnownUpdate); // 角标点开直接用它，不重新联网。
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("installed")]
    [InlineData("upToDate")]
    public async Task SavedVersionIsNotRestoredWhenSkippedInstalledOrSuperseded(string scenario)
    {
        var preferences = new Preferences();
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyKnownUpdate, Info());
        if (scenario == "upToDate")
        {
            var checking = new UpdateCoordinator(new RecordingService { Result = UpdateCheckResult.UpToDate() }, preferences)
            { StartupDelay = TimeSpan.Zero };
            await checking.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"));
        }
        else await preferences.SetPreferenceAsync(UpdateCoordinator.KeyLastCheckUtc, DateTime.UtcNow);
        if (scenario == "skipped") await preferences.SetPreferenceAsync(UpdateCoordinator.KeySkippedVersion, "2.0.0");

        var service = new RecordingService
        {
            Options = new() { CurrentVersion = scenario == "installed" ? "2.0.0" : "1.0.0", PackageKind = AppPackageKind.Installer }
        };
        var coordinator = new UpdateCoordinator(service, preferences);
        var restored = 0;
        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"),
            _ => { restored++; return Task.CompletedTask; });

        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, restored);
        Assert.Equal(scenario == "skipped", await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate) is not null);
    }

    [Fact]
    public async Task LongRunningInstanceRechecksEveryIntervalAndRemindsAgain()
    {
        var ticks = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var service = new RecordingService();
        var coordinator = new UpdateCoordinator(service, new Preferences())
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20),
            UtcNow = () => new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc)
        };
        var prompts = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource();
        var loop = coordinator.RunAutoCheckLoopAsync(_ => { prompts.Release(); return Task.CompletedTask; }, null, cts.Token);
        try
        {
            Assert.True(await prompts.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, service.CheckCalls);

            // 未满 24 小时：轮询多次也不联网。
            Interlocked.Add(ref ticks, TimeSpan.FromHours(23).Ticks);
            await Task.Delay(300);
            Assert.Equal(1, service.CheckCalls);

            // 满 24 小时后重新检查，"稍后提醒"的版本再提示一次。
            Interlocked.Add(ref ticks, TimeSpan.FromHours(2).Ticks);
            Assert.True(await prompts.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(2, service.CheckCalls);
        }
        finally { cts.Cancel(); }
        await loop.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, service.DownloadCalls); Assert.Equal(0, service.ApplyCalls);
    }

    [Fact]
    public async Task PartialPlatformFailureRetriesHourlyButPromptsOncePerInterval()
    {
        var ticks = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var service = new RecordingService { Result = UpdateCheckResult.Available(Info()) with { WarningMessage = "GitHub 无法连接" } };
        var coordinator = new UpdateCoordinator(service, new Preferences())
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20),
            UtcNow = () => new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc)
        };
        var prompts = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource();
        var loop = coordinator.RunAutoCheckLoopAsync(_ => { prompts.Release(); return Task.CompletedTask; }, null, cts.Token);
        try
        {
            Assert.True(await prompts.WaitAsync(TimeSpan.FromSeconds(15)));

            // 没记检查时间，轮询会继续联网，但同一版本不再弹窗。
            Interlocked.Add(ref ticks, TimeSpan.FromHours(2).Ticks);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (service.CheckCalls < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(service.CheckCalls >= 3);
            Assert.Equal(0, prompts.CurrentCount);

            Interlocked.Add(ref ticks, TimeSpan.FromHours(23).Ticks);
            Assert.True(await prompts.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally { cts.Cancel(); }
        await loop.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task LaterUpToDateCheckClearsKnownUpdateAndNotifiesBadge()
    {
        var preferences = new Preferences();
        var coordinator = new UpdateCoordinator(new SequenceService(UpdateCheckResult.Available(Info()), UpdateCheckResult.UpToDate()), preferences);
        var cleared = 0;
        coordinator.KnownUpdateCleared += () => cleared++;

        await coordinator.CheckManuallyAsync();
        Assert.NotNull(coordinator.LastKnownUpdate);
        Assert.Equal(0, cleared);

        await coordinator.CheckManuallyAsync();
        Assert.Null(coordinator.LastKnownUpdate);
        Assert.Null(await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate));
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task LastCheckInTheFutureIsTreatedAsDue()
    {
        var preferences = new Preferences();
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyLastCheckUtc, DateTime.UtcNow.AddDays(30));
        var service = new RecordingService { Result = UpdateCheckResult.UpToDate() };
        var coordinator = new UpdateCoordinator(service, preferences) { StartupDelay = TimeSpan.Zero };
        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"));
        Assert.Equal(1, service.CheckCalls);
    }

    private static UpdateInfo Info() => new()
    {
        Version = "2.0.0", TagName = "v2.0.0", HtmlUrl = "https://example.invalid/release",
        FileName = "setup.exe", DownloadUrl = "https://example.invalid/setup.exe",
        ReleaseNotes = "<!-- user-notes:start -->改善更新提示，是否安装由您决定。<!-- user-notes:end -->\n开发详情"
    };

    private sealed class Preferences : IUserPreferencesService
    {
        private readonly ConcurrentDictionary<string, object> _values = new();
        public Task<T?> GetPreferenceAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? (T?)value : default);
        public Task SetPreferenceAsync<T>(string key, T value) { _values[key] = value!; return Task.CompletedTask; }
    }

    private sealed class SequenceService(params UpdateCheckResult[] results) : IUpdateService
    {
        private int _next;
        public UpdateOptions Options { get; } = new() { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Installer };
        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default) =>
            Task.FromResult(results[Math.Min(_next++, results.Length - 1)]);
        public Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("should not download");
        public bool TryApplyUpdate(UpdateInfo info, string localFilePath) => throw new InvalidOperationException("should not install");
        public void OpenReleasePage(UpdateInfo? info = null) { }
        public void CleanupDownloads() { }
    }

    private sealed class RecordingService : IUpdateService
    {
        public UpdateOptions Options { get; init; } = new() { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Installer };
        public UpdateCheckResult Result { get; init; } = UpdateCheckResult.Available(Info());
        public bool ThrowOnCheck { get; init; }
        public Task<UpdateCheckResult>? PendingResponse { get; init; }
        public TaskCompletionSource CheckEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
        {
            CheckCalls++; CheckEntered.TrySetResult();
            if (ThrowOnCheck) throw new HttpRequestException("private endpoint detail");
            return PendingResponse ?? Task.FromResult(Result);
        }
        public Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        { DownloadCalls++; throw new InvalidOperationException("should not download"); }
        public bool TryApplyUpdate(UpdateInfo info, string localFilePath)
        { ApplyCalls++; throw new InvalidOperationException("should not install"); }
        public void OpenReleasePage(UpdateInfo? info = null) { }
        public void CleanupDownloads() { }
    }
}
