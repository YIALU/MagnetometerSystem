using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Services;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.App.Tests;

public class UpdateSourceUiTests
{
    [Fact]
    public Task SourcePreferenceSurvivesRestartAndAppSettingsSave() => WpfTestHost.RunAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "update-preference-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var db = new DatabaseInitializer(Path.Combine(directory, "test.db")); await db.InitializeAsync();
            var preferences = new UserPreferencesService(db);
            using var service = new MultiPlatformUpdateService(new() { CurrentVersion = "0.5.0", PackageKind = AppPackageKind.Portable });
            var coordinator = new UpdateCoordinator(service, preferences);
            var config = new AppConfigService(db);
            var vm = new SettingsViewModel(config, coordinator);
            await vm.EnsureLoadedAsync();
            Assert.Equal(UpdateSource.Automatic, vm.UpdateSource);
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 700, Height = 820, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show(); await WpfTestHost.PumpAsync();
                var combo = Descendants<ComboBox>(view).Single(x => x.ItemsSource == vm.UpdateSources);
                combo.SelectedValue = UpdateSource.GitHub;
                await WpfTestHost.PumpAsync();
                Assert.Equal(UpdateSource.GitHub, vm.UpdateSource);
                await coordinator.GetSourceAsync(); // 等待即刻保存完成。
                await vm.SaveSettingsCommand.ExecuteAsync(null);
                await coordinator.SetAutoCheckEnabledAsync(false);
                await config.SaveSettingsAsync(new()); // 模拟退出时写回全局设置。
                using var restarted = new MultiPlatformUpdateService(new() { CurrentVersion = "0.5.0", PackageKind = AppPackageKind.Portable });
                var next = new UpdateCoordinator(restarted, new UserPreferencesService(db));
                Assert.Equal(UpdateSource.GitHub, await next.GetSourceAsync());
                Assert.False(await next.IsAutoCheckEnabledAsync());
                Assert.Equal(UpdateSource.GitHub, restarted.Options.PreferredSource);
                SaveScreenshot(view, "settings-update-sources.png");
                vm.ResetToDefaultsCommand.Execute(null);
                Assert.Equal(UpdateSource.Automatic, await coordinator.GetSourceAsync());
                Assert.Equal("Automatic", await preferences.GetPreferenceAsync<string>(UpdateCoordinator.KeySource));
            }
            finally { window.Close(); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task UpdateDialogSwitchesPlatformNotesAndDownloadTargetWithoutMixingSources() => WpfTestHost.RunAsync(async () =>
    {
        using var service = new MultiPlatformUpdateService(new() { CurrentVersion = "0.4.0", PackageKind = AppPackageKind.Installer });
        var github = Info(UpdateSource.GitHub);
        var gitee = Info(UpdateSource.Gitee) with { Mirrors = [github] };
        var dialog = new UpdateDialog(service, gitee) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            dialog.Show(); await WpfTestHost.PumpAsync();
            var selector = (ComboBox)dialog.FindName("SourceSelector");
            selector.SelectedItem = github;
            await WpfTestHost.PumpAsync();
            Assert.Contains("GitHub", ((TextBlock)dialog.FindName("ReleaseNotesText")).Text);
            var summary = ((TextBlock)dialog.FindName("UpdateSummaryText")).Text;
            Assert.Contains("GitHub", summary);
            Assert.DoesNotContain("开发详情", summary);
            var selected = (UpdateInfo)typeof(UpdateDialog).GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Assert.Equal(github.DownloadUrl, selected.DownloadUrl);
            Assert.Equal(github.ChecksumsUrl, selected.ChecksumsUrl);
            Assert.Equal(UpdateSource.Gitee, Assert.Single(selected.Mirrors).Source);
            typeof(UpdateDialog).GetMethod("SetDownloadingState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [true]);
            Assert.False(selector.IsEnabled);
            typeof(UpdateDialog).GetMethod("SetDownloadingState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [false]);
            Assert.True(selector.IsEnabled);
            SaveScreenshot((FrameworkElement)dialog.Content, "update-github.png");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task AboutDialogDisplaysBothProjectLinks() => WpfTestHost.RunAsync(async () =>
    {
        var dialog = new AboutDialog { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            dialog.Show(); await WpfTestHost.PumpAsync();
            Assert.Equal("github.com", ((Hyperlink)dialog.FindName("GitHubLink")).NavigateUri.Host);
            Assert.Equal("gitee.com", ((Hyperlink)dialog.FindName("HomepageLink")).NavigateUri.Host);
            SaveScreenshot((FrameworkElement)dialog.Content, "about-project-links.png");
        }
        finally { dialog.Close(); }
    });

    private static UpdateInfo Info(UpdateSource source) => new()
    {
        Source = source, Version = "0.5.0", TagName = "v0.5.0", ReleaseNotes = $"<!-- user-notes:start -->{source} 使用体验改善<!-- user-notes:end -->\n{source} 开发详情",
        HtmlUrl = $"https://{source.ToString().ToLowerInvariant()}.com/releases/tag/v0.5.0",
        FileName = "MagnetometerSystem-v0.5.0-setup.exe",
        DownloadUrl = $"https://{source.ToString().ToLowerInvariant()}.com/setup.exe",
        ChecksumsUrl = $"https://{source.ToString().ToLowerInvariant()}.com/SHA256SUMS.txt"
    };
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var value2 in Descendants<T>(child)) yield return value2;
        }
    }
    internal static void SaveScreenshot(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            context.DrawRectangle(Brushes.White, null, bounds);
            context.DrawRectangle(new VisualBrush(root), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
