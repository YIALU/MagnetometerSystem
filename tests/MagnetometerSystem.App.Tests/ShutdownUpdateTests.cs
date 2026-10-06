using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;
using AppSettings = MagnetometerSystem.Infrastructure.Configuration.AppSettings;

namespace MagnetometerSystem.App.Tests;

public class ShutdownUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UpdateWaitsForStorageAndSettingsBeforeInstallerAndHonorsCancellation(bool cancel) => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var stopping = NewCompletion();
        var releaseStorage = NewCompletion();
        fixture.Bus.AcquisitionStopping += async () => { stopping.TrySetResult(); await releaseStorage.Task; };
        try
        {
            await fixture.Main.ConnectionVM.ConnectCommand.ExecuteAsync(null);
            fixture.Connection.Feed("1,2,3\n");
            fixture.Connection.DisconnectFrame = "7,8,9\n";
            fixture.ClickUpdate();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, fixture.Updates.ApplyCalls);
            Assert.False(fixture.Window.IsEnabled);
            Assert.False(fixture.Connection.IsConnected);
            releaseStorage.TrySetResult();
            await fixture.Settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, fixture.Updates.ApplyCalls);
            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.NotNull(session.EndedAt);
            var saved = await fixture.Storage.GetReadingsAsync(session.Id);
            Assert.Equal(2, saved.Count);
            Assert.Equal(7, saved[1].ChannelValues[0]);
            if (cancel) ((Button)fixture.Dialog.FindName("CancelDownloadButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fixture.Settings.ReleaseSave.TrySetResult();
            await WaitForAsync(() => fixture.Window.IsEnabled);
            Assert.Equal(cancel ? 0 : 1, fixture.Updates.ApplyCalls);
            Assert.Contains(cancel ? "已取消" : "更新包已下载，但未能开始更新", fixture.Status);
            Assert.False(fixture.Main.ConnectionVM.IsAcquiring);
        }
        finally { releaseStorage.TrySetResult(); fixture.Settings.ReleaseSave.TrySetResult(); }
    });

    [Fact]
    public Task StorageFailureBlocksInstallerAndRetryCanFinishDespiteOldUiError() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        bool fail = true;
        fixture.Settings.ReleaseSave.TrySetResult();
        fixture.Bus.AcquisitionStopping += () => fail
            ? Task.FromException(new IOException("测试落库失败")) : Task.CompletedTask;
        try
        {
            await fixture.Main.ConnectionVM.ConnectCommand.ExecuteAsync(null);
            fixture.Connection.Feed("4,5,6\n");
            fixture.ClickUpdate();
            await WaitForAsync(() => fixture.Status.Contains("数据或设置未能全部保存"));
            Assert.Equal(0, fixture.Updates.ApplyCalls);
            Assert.False(fixture.Settings.SaveEntered.Task.IsCompleted);
            Assert.True(fixture.Window.IsEnabled);
            Assert.True(fixture.Main.ConnectionVM.IsAcquiring);
            fail = false;
            fixture.Main.SessionListVM.StorageError = "旧的界面错误文本";
            fixture.ClickUpdate();
            await WaitForAsync(() => fixture.Updates.ApplyCalls == 1 && fixture.Window.IsEnabled);
            Assert.False(fixture.Main.ConnectionVM.IsAcquiring);
            Assert.Null(fixture.Main.SessionListVM.ActiveSessionId);
            Assert.Contains("更新包已下载，但未能开始更新", fixture.Status);
        }
        finally { fail = false; }
    });

    [Fact]
    public Task ActualSqliteWriteFailurePreventsInstallerAndPreservesSessionForRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.ReleaseSave.TrySetResult();
        await fixture.Main.ConnectionVM.ConnectCommand.ExecuteAsync(null);
        await using var database = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await database.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = database.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        bool renamed = false;
        try
        {
            await Execute("ALTER TABLE readings RENAME TO temporarily_unavailable_readings");
            renamed = true;
            fixture.Connection.Feed("4,5,6\n");
            fixture.ClickUpdate();
            await WaitForAsync(() => fixture.Main.SessionListVM.StorageError != null && fixture.Window.IsEnabled);
            Assert.Equal(0, fixture.Updates.ApplyCalls);
            Assert.False(fixture.Settings.SaveEntered.Task.IsCompleted);
            Assert.NotNull(fixture.Main.SessionListVM.ActiveSessionId);
            await Execute("ALTER TABLE temporarily_unavailable_readings RENAME TO readings");
            renamed = false;
            // The workbench exposes an explicit retry command; WaitForPendingWrites never hides a prior failure.
            await fixture.Main.SessionListVM.RetryStorageCommand.ExecuteAsync(null);
            Assert.Equal(0, fixture.Storage.WriteStatus.PendingReadings);
            fixture.ClickUpdate();
            await WaitForAsync(() => fixture.Updates.ApplyCalls == 1 && fixture.Window.IsEnabled);
            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.Single(await fixture.Storage.GetReadingsAsync(session.Id));
            Assert.NotNull(session.EndedAt);
        }
        finally
        {
            if (renamed)
            {
                await Execute("ALTER TABLE temporarily_unavailable_readings RENAME TO readings");
                await fixture.Main.SessionListVM.RetryStorageCommand.ExecuteAsync(null);
            }
        }
    });

    [Fact]
    public Task NormalWindowCloseWaitsForTailAndSettings() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Main.CurrentView = null; // Keep the connection page from loading personal saved protocols in this UI test.
        fixture.Window.ShowInTaskbar = false;
        fixture.Window.ShowActivated = false;
        fixture.Window.Left = -10000;
        fixture.Window.Top = -10000;
        fixture.Window.Show();
        await fixture.Main.ConnectionVM.ConnectCommand.ExecuteAsync(null);
        fixture.Connection.Feed("1,2,3\n");
        fixture.Connection.DisconnectFrame = "7,8,9\n";
        fixture.Window.Close();
        try
        {
            await fixture.Settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Window.IsVisible);
            Assert.False(fixture.Window.IsEnabled);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)fixture.Window.FindName("ExitProgressOverlay")).Visibility);
            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.NotNull(session.EndedAt);
            Assert.Equal(2, (await fixture.Storage.GetReadingsAsync(session.Id)).Count);
            fixture.Settings.ReleaseSave.TrySetResult();
            await WaitForAsync(() => !fixture.Window.IsVisible);
            Assert.Equal(0, fixture.Updates.ApplyCalls);
        }
        finally { fixture.Settings.ReleaseSave.TrySetResult(); }
    });

    [Fact]
    public Task ImmediatelyCompletedSaveClosesAfterOriginalClosingEventReturns() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.ReleaseSave.TrySetResult();
        fixture.Main.CurrentView = null;
        // 没有活动会话，配置服务立即完成：确保覆盖此前遗漏的同步分支。
        var saved = App.PrepareForExitAsync(fixture.Main, fixture.Settings);
        Assert.True(saved.IsCompletedSuccessfully);
        fixture.Window.ShowInTaskbar = false;
        fixture.Window.ShowActivated = false;
        fixture.Window.Left = -10000;
        fixture.Window.Top = -10000;
        fixture.Window.Show();
        var closingEvents = 0;
        fixture.Window.Closing += (_, _) => closingEvents++;
        fixture.Window.Close();
        Assert.Equal(1, closingEvents);
        Assert.True(fixture.Window.IsVisible);
        await WaitForAsync(() => !fixture.Window.IsVisible);
        Assert.Equal(2, closingEvents);
        Assert.True(fixture.Settings.SaveEntered.Task.IsCompleted);
        Assert.Equal(0, fixture.Updates.ApplyCalls);
    });

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(predicate(), "等待更新/退出界面状态超时");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required DataBus Bus { get; init; }
        public required MainViewModel Main { get; init; }
        public required MainWindow Window { get; init; }
        public required UpdateDialog Dialog { get; init; }
        public required SqliteStorageService Storage { get; init; }
        public required TestConnection Connection { get; init; }
        public required DelayedSettings Settings { get; init; }
        public required RecordingUpdateService Updates { get; init; }
        public required string DatabasePath { get; init; }
        public Window? PreviousMainWindow { get; init; }
        public string Status => ((TextBlock)Dialog.FindName("StatusText")).Text;
        public void ClickUpdate() => ((Button)Dialog.FindName("UpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public static async Task<Fixture> CreateAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"shutdown_{Guid.NewGuid():N}.db");
            var database = new DatabaseInitializer(path);
            await database.InitializeAsync();
            var bus = new DataBus();
            var storage = new SqliteStorageService(database, bus);
            var profiles = new SqliteCalibrationRepository(database);
            var corrector = new OrthogonalityCorrector();
            var settings = new DelayedSettings();
            var updates = new RecordingUpdateService();
            var connection = new TestConnection();
            var connectionVm = new ConnectionViewModel(new TestConnectionFactory(connection), bus, corrector, profiles);
            var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, profiles);
            var chart = new RealtimeChartViewModel(bus);
            var history = new HistoryPlaybackViewModel(storage, bus, corrector, profiles);
            var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), profiles, bus, storage);
            var commands = new DeviceCommandViewModel(bus, settings);
            var settingsVm = new SettingsViewModel(settings, new UpdateCoordinator(updates, new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(database)));
            var main = new MainViewModel(connectionVm, chart, sessions, history, ortho, new SensorCalibrationViewModel(profiles), settingsVm, commands, bus);
            var previous = Application.Current.MainWindow;
            var window = new MainWindow(settings) { DataContext = main };
            Application.Current.MainWindow = window;
            var dialog = new UpdateDialog(updates, new UpdateInfo
            {
                Version = "2.0.0", TagName = "v2.0.0", HtmlUrl = "https://example.invalid/release",
                DownloadUrl = "https://example.invalid/setup.exe", FileName = "setup.exe",
            });
            return new Fixture
            {
                Bus = bus, Main = main, Window = window, Dialog = dialog, Storage = storage,
                Connection = connection, Settings = settings, Updates = updates, DatabasePath = path,
                PreviousMainWindow = previous,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Settings.ReleaseSave.TrySetResult();
            await Main.ConnectionVM.StopAcquisitionAsync();
            Dialog.Close();
            Window.DataContext = null;
            Window.Close();
            Application.Current.MainWindow = PreviousMainWindow;
            Main.OrthoCalibVM.Cleanup();
            Main.DeviceCommandVM.Dispose();
            Main.HistoryPlaybackVM.Dispose();
            Main.RealtimeChartVM.Dispose();
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(DatabasePath + suffix);
        }
    }

    private sealed class DelayedSettings : IAppConfigService
    {
        public TaskCompletionSource SaveEntered { get; } = NewCompletion();
        public TaskCompletionSource ReleaseSave { get; } = NewCompletion();
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());
        public async Task SaveSettingsAsync(AppSettings settings) { SaveEntered.TrySetResult(); await ReleaseSave.Task; }
    }

    private sealed class RecordingUpdateService : IUpdateService
    {
        public int ApplyCalls { get; private set; }
        public UpdateOptions Options { get; } = new() { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Installer };
        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default) => Task.FromResult(UpdateCheckResult.UpToDate());
        public Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default) => Task.FromResult("unused-test-installer.exe");
        public bool TryApplyUpdate(UpdateInfo info, string localFilePath) { ApplyCalls++; throw new IOException("测试安装器未启动"); }
        public void OpenReleasePage(UpdateInfo? info = null) { }
        public void CleanupDownloads() { }
    }

    private sealed class TestConnectionFactory(TestConnection connection) : IConnectionFactory
    {
        public IDeviceConnection Create(ConnectionConfig config) => connection;
    }

    private sealed class TestConnection : IDeviceConnection
    {
        public event EventHandler<byte[]>? DataReceived;
        public event EventHandler<string>? ErrorOccurred { add { } remove { } }
        public event EventHandler<bool>? ConnectionStateChanged;
        public bool IsConnected { get; private set; }
        public ConnectionConfig Config { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; ConnectionStateChanged?.Invoke(this, true); return Task.CompletedTask; }
        public string? DisconnectFrame { get; set; }
        public Task DisconnectAsync() { if (DisconnectFrame is { } frame) Feed(frame); DisconnectFrame = null; IsConnected = false; ConnectionStateChanged?.Invoke(this, false); return Task.CompletedTask; }
        public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Feed(string text) => DataReceived?.Invoke(this, Encoding.ASCII.GetBytes(text));
    }
}
