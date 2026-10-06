using System.Collections.Concurrent;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Services;
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

    private sealed class RecordingService : IUpdateService
    {
        public UpdateOptions Options { get; } = new() { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Installer };
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
