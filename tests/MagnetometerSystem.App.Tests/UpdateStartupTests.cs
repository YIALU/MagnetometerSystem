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
        // 切换到 GitHub 时写完平台偏好就退出，留下 Gitee 的缓存。
        if (scenario == "otherSource") await preferences.SetPreferenceAsync(UpdateCoordinator.KeySource, "GitHub");
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
    [InlineData("otherSource")]
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

        var found = (await coordinator.CheckManuallyAsync()).Info!;
        Assert.NotNull(coordinator.LastKnownUpdate);
        Assert.True(coordinator.IsCurrentUpdate(found));
        Assert.Equal(0, cleared);

        await coordinator.CheckManuallyAsync();
        Assert.Null(coordinator.LastKnownUpdate);
        Assert.False(coordinator.IsCurrentUpdate(found)); // 排队中的旧回调据此不再挂角标。
        Assert.Null(await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate));
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task SwitchingSourceDropsCachedUpdateAndMakesCheckDue()
    {
        var preferences = new Preferences();
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyLastCheckUtc, DateTime.UtcNow);
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyKnownUpdate, Info() with { Source = UpdateSource.GitHub });
        var service = new RecordingService { Result = UpdateCheckResult.UpToDate() };
        var coordinator = new UpdateCoordinator(service, preferences) { StartupDelay = TimeSpan.Zero };
        var cleared = 0;
        coordinator.KnownUpdateCleared += () => cleared++;
        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"), _ => Task.CompletedTask);
        Assert.NotNull(coordinator.LastKnownUpdate);

        await coordinator.SetSourceAsync(UpdateSource.Automatic); // 未变化：缓存保留。
        Assert.NotNull(coordinator.LastKnownUpdate);
        await coordinator.SetSourceAsync(UpdateSource.Gitee);

        Assert.Null(coordinator.LastKnownUpdate);
        Assert.Equal(1, cleared);
        Assert.Null(await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate));
        Assert.Null(await preferences.GetPreferenceAsync<DateTime?>(UpdateCoordinator.KeyLastCheckUtc));
        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"));
        Assert.Equal(1, service.CheckCalls);
    }

    [Fact]
    public async Task ResultFromBeforeSourceSwitchIsDiscarded()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingService { PendingResponse = response.Task };
        var preferences = new Preferences();
        var coordinator = new UpdateCoordinator(service, preferences);
        var pending = coordinator.CheckManuallyAsync();
        Task switching;
        try
        {
            await service.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // 切换立即生效，清除缓存要等这次检查结束。
            switching = coordinator.SetSourceAsync(UpdateSource.GitHub);
            Assert.False(switching.IsCompleted);
        }
        finally { response.TrySetResult(UpdateCheckResult.Available(Info())); }
        await pending;
        await switching.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(coordinator.LastKnownUpdate);
        Assert.Null(await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate));
        Assert.Null(await preferences.GetPreferenceAsync<DateTime?>(UpdateCoordinator.KeyLastCheckUtc));
    }

    [Fact]
    public async Task ConcurrentChecksRunOneAtATime()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingService { PendingResponse = response.Task };
        var coordinator = new UpdateCoordinator(service, new Preferences());
        var first = coordinator.CheckManuallyAsync();
        Task<UpdateCheckResult>? second = null;
        try
        {
            await service.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = coordinator.CheckManuallyAsync();
            await Task.Delay(200);
            Assert.Equal(1, service.CheckCalls);
        }
        finally { response.TrySetResult(UpdateCheckResult.Available(Info())); }
        await first;
        await second!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, service.CheckCalls);
    }

    [Fact]
    public Task ManuallyShownVersionIsNotAutoPromptedAgainWithinInterval() => WpfTestHost.RunAsync(async () =>
    {
        var service = new RecordingService { Result = UpdateCheckResult.Available(Info()) with { WarningMessage = "GitHub 无法连接" } };
        var coordinator = new UpdateCoordinator(service, new Preferences()) { StartupDelay = TimeSpan.Zero };

        // "关于"里手动检查发现新版本并弹窗，用户点"稍后提醒"关掉。
        var manual = await coordinator.CheckManuallyAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); Application.Current.Windows.OfType<UpdateDialog>().Single().Close(); };
        try { timer.Start(); await coordinator.ShowUpdateDialogAsync(null, manual.Info!); }
        finally { timer.Stop(); }

        // 部分平台失败没记检查时间，随后的自动检查仍会联网，但不再弹同一版本。
        var prompts = 0;
        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        Assert.Equal(2, service.CheckCalls);
        Assert.Equal(0, prompts);
    });

    [Fact]
    public async Task SourceSwitchAfterRecordingSuppressesPromptAndClearsResult()
    {
        var preferences = new Preferences();
        var service = new RecordingService();
        UpdateCoordinator coordinator = null!;
        var gate = new SwitchingPreferences(preferences, UpdateCoordinator.KeySkippedVersion,
            () => coordinator.SetSourceAsync(UpdateSource.GitHub));
        coordinator = new UpdateCoordinator(service, gate) { StartupDelay = TimeSpan.Zero };
        var prompts = 0;

        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        await gate.Switched!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, prompts);
        Assert.Null(coordinator.LastKnownUpdate);
        Assert.Null(await preferences.GetPreferenceAsync<UpdateInfo>(UpdateCoordinator.KeyKnownUpdate));
        Assert.Null(await preferences.GetPreferenceAsync<DateTime?>(UpdateCoordinator.KeyLastCheckUtc));
    }

    [Fact]
    public async Task NewerManualResultSuppressesPendingSilentPrompt()
    {
        var preferences = new Preferences();
        var service = new SequenceService(UpdateCheckResult.Available(Info()), UpdateCheckResult.UpToDate());
        UpdateCoordinator coordinator = null!;
        Task<UpdateCheckResult>? manual = null;
        var gate = new SwitchingPreferences(preferences, UpdateCoordinator.KeySkippedVersion,
            async () => { manual = coordinator.CheckManuallyAsync(); await manual; });
        coordinator = new UpdateCoordinator(service, gate) { StartupDelay = TimeSpan.Zero };
        var prompts = 0;

        // 定时检查写入"有新版本"后、弹窗前，手动检查确认已是最新。
        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });

        Assert.Equal(UpdateCheckStatus.UpToDate, (await manual!).Status);
        Assert.Equal(0, prompts);
        Assert.Null(coordinator.LastKnownUpdate);
    }

    [Fact]
    public async Task CheckStartedDuringSourceSwitchRunsAfterItAndKeepsItsResult()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = new BlockingSetPreferences(new Preferences(), UpdateCoordinator.KeySource, saving.Task);
        var service = new RecordingService();
        var coordinator = new UpdateCoordinator(service, preferences);
        var cleared = 0;
        coordinator.KnownUpdateCleared += () => cleared++;

        var switching = coordinator.SetSourceAsync(UpdateSource.GitHub);
        var manual = coordinator.CheckManuallyAsync();
        try
        {
            await Task.Delay(200);
            Assert.Equal(0, service.CheckCalls); // 切换和清缓存做完之前不开始新检查。
        }
        finally { saving.TrySetResult(); }
        await switching.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await manual.WaitAsync(TimeSpan.FromSeconds(15))).Status);

        Assert.Equal(1, service.CheckCalls);
        Assert.NotNull(coordinator.LastKnownUpdate); // 新平台的结果没被清掉。
        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task SavedVersionIsRestoredWhenDueCheckFails()
    {
        var preferences = new Preferences();
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyLastCheckUtc, DateTime.UtcNow.AddHours(-30));
        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyKnownUpdate, Info());
        var service = new RecordingService { Result = UpdateCheckResult.Failed("offline") };
        var coordinator = new UpdateCoordinator(service, preferences) { StartupDelay = TimeSpan.Zero };
        UpdateInfo? restored = null;

        await coordinator.RunStartupCheckAsync(_ => throw new InvalidOperationException("unexpected prompt"),
            info => { restored = info; return Task.CompletedTask; });

        Assert.Equal(1, service.CheckCalls);
        Assert.Equal("2.0.0", restored?.Version);
        Assert.True(coordinator.IsCurrentUpdate(restored!));
    }

    [Fact]
    public async Task PromptCooldownSurvivesRestartAfterPartialFailure()
    {
        var preferences = new Preferences();
        var partial = UpdateCheckResult.Available(Info()) with { WarningMessage = "GitHub 无法连接" };
        var first = new UpdateCoordinator(new RecordingService { Result = partial }, preferences) { StartupDelay = TimeSpan.Zero };
        var prompts = 0;
        await first.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        Assert.Equal(1, prompts); // 点"稍后提醒"后重启；没写检查时间，启动时会再查。

        var service = new RecordingService { Result = partial };
        var restarted = new UpdateCoordinator(service, preferences) { StartupDelay = TimeSpan.Zero };
        await restarted.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task SourceSwitchDropsOldUpdateBeforeSavingPreference()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = new BlockingSetPreferences(new Preferences(), UpdateCoordinator.KeySource, saving.Task);
        var coordinator = new UpdateCoordinator(new RecordingService(), preferences);
        var found = (await coordinator.CheckManuallyAsync()).Info!;
        Assert.True(coordinator.IsCurrentUpdate(found));

        var switching = coordinator.SetSourceAsync(UpdateSource.GitHub);
        try
        {
            Assert.False(switching.IsCompleted);
            Assert.False(coordinator.IsCurrentUpdate(found)); // 保存平台偏好期间，排队的旧回调已不能挂角标。
        }
        finally { saving.TrySetResult(); }
        await switching.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task PromptCooldownStillPublishesBadgeAfterSourceSwitch()
    {
        var preferences = new Preferences();
        var coordinator = new UpdateCoordinator(new RecordingService(), preferences) { StartupDelay = TimeSpan.Zero };
        var prompts = 0;
        var badges = 0;
        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; }, _ => { badges++; return Task.CompletedTask; });
        Assert.Equal(1, prompts);

        // 换平台撤下角标；新平台查到同一版本，冷却内不再弹窗，但角标要挂回来。
        await coordinator.SetSourceAsync(UpdateSource.GitHub);
        await coordinator.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; }, _ => { badges++; return Task.CompletedTask; });
        Assert.Equal(1, prompts);
        Assert.Equal(1, badges);
        Assert.NotNull(coordinator.LastKnownUpdate);
    }

    [Fact]
    public async Task PromptCooldownLoadsEvenWhenAutoCheckDisabledAtStartup()
    {
        var preferences = new Preferences();
        var partial = UpdateCheckResult.Available(Info()) with { WarningMessage = "GitHub 无法连接" };
        var first = new UpdateCoordinator(new RecordingService { Result = partial }, preferences) { StartupDelay = TimeSpan.Zero };
        var prompts = 0;
        await first.RunStartupCheckAsync(_ => { prompts++; return Task.CompletedTask; });
        Assert.Equal(1, prompts);

        await preferences.SetPreferenceAsync(UpdateCoordinator.KeyAutoCheck, false);
        var ticks = DateTime.UtcNow.Ticks;
        var service = new RecordingService { Result = partial };
        var restarted = new UpdateCoordinator(service, preferences)
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20),
            UtcNow = () => new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc)
        };
        using var cts = new CancellationTokenSource();
        var loop = restarted.RunAutoCheckLoopAsync(_ => { prompts++; return Task.CompletedTask; }, null, cts.Token);
        try
        {
            await restarted.SetAutoCheckEnabledAsync(true); // 运行中重新打开自动检查。
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (service.CheckCalls < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(service.CheckCalls >= 2);
            Assert.Equal(1, prompts);
        }
        finally { cts.Cancel(); }
        await loop.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task SameVersionFromNewerCheckSupersedesOlderResult()
    {
        var gitee = Info();
        var github = Info() with { Source = UpdateSource.GitHub, DownloadUrl = "https://example.invalid/gh.exe" };
        var coordinator = new UpdateCoordinator(
            new SequenceService(UpdateCheckResult.Available(gitee), UpdateCheckResult.Available(github)), new Preferences());
        await coordinator.CheckManuallyAsync();
        Assert.True(coordinator.IsCurrentUpdate(gitee));
        await coordinator.CheckManuallyAsync();
        Assert.False(coordinator.IsCurrentUpdate(gitee)); // 同版本、另一平台的新结果取代了旧结果。
        Assert.True(coordinator.IsCurrentUpdate(github));
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

    /// <summary>第一次读到指定键时完成一次平台切换，模拟用户在结果写入后、弹窗前换平台。</summary>
    private sealed class SwitchingPreferences(Preferences inner, string triggerKey, Func<Task> onTrigger) : IUserPreferencesService
    {
        public Task? Switched { get; private set; }
        public async Task<T?> GetPreferenceAsync<T>(string key)
        {
            if (key == triggerKey && Switched is null)
            {
                Switched = onTrigger();
                await Switched;
            }
            return await inner.GetPreferenceAsync<T>(key);
        }
        public Task SetPreferenceAsync<T>(string key, T value) => inner.SetPreferenceAsync(key, value);
    }

    /// <summary>写指定键时等外部放行，模拟切换平台时保存偏好较慢。</summary>
    private sealed class BlockingSetPreferences(Preferences inner, string blockedKey, Task release) : IUserPreferencesService
    {
        public Task<T?> GetPreferenceAsync<T>(string key) => inner.GetPreferenceAsync<T>(key);
        public async Task SetPreferenceAsync<T>(string key, T value)
        {
            if (key == blockedKey) await release;
            await inner.SetPreferenceAsync(key, value);
        }
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
