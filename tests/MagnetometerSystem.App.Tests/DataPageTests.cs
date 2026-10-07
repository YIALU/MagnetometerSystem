using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using MagnetometerSystem.Infrastructure.Update;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class DataPageTests
{
    /// <summary>
    /// 在完整主窗口里走一遍数据页：列表与回放两层互斥、刷新不丢选中和导出勾选、
    /// 筛选为空时有说明、结束日期当天的会话不被漏掉。
    /// </summary>
    [Fact]
    public Task ListAndPlaybackAreExclusive_RefreshKeepsSelection_FiltersExplainEmptyResults() => WpfTestHost.RunAsync(async () =>
    {
        var shots = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
        var dbPath = Path.Combine(Path.GetTempPath(), $"datapage-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var bus = new DataBus(); var storage = new SqliteStorageService(db, bus);
        var repo = new SqliteCalibrationRepository(db); var corrector = new OrthogonalityCorrector();
        var config = new AppConfigService(db);
        using var chart = new RealtimeChartViewModel(bus);
        var connection = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, repo);
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repo);
        using var history = new HistoryPlaybackViewModel(storage, bus, corrector, repo);
        var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), repo, bus, storage);
        var commands = new DeviceCommandViewModel(bus, config);
        var settings = new SettingsViewModel(config, new UpdateCoordinator(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }), new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db)));
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, commands, bus, new AnalysisViewModel(storage));
        main.IsInitialized = true;

        var names = new[] { "X1", "Y1", "Z1", "X2", "Y2", "Z2" };
        var gradient = await storage.StartSessionAsync("梯度六通道", new SensorConfig
        {
            Type = SensorType.Generic, SampleRate = 100, ChannelCountOverride = 6,
            ChannelNamesOverride = names, ChannelUnitsOverride = Enumerable.Repeat("nT", 6).ToArray(),
        }, new ConnectionConfig());
        var t0 = DateTime.Now.AddSeconds(-40);
        await storage.SaveReadingsAsync(Enumerable.Range(0, 3000).Select(i => new MagnetometerReading
        {
            SessionId = gradient, Timestamp = t0.AddMilliseconds(i * 10), SensorType = SensorType.Generic,
            ChannelValues = Enumerable.Range(0, 6).Select(c => 50000 + c * 100 + 20 * Math.Sin(i * 0.02 + c)).ToArray(),
        }));
        await storage.EndSessionAsync(gradient);
        var withTemperature = await storage.StartSessionAsync("带温度", new SensorConfig
        {
            Type = SensorType.Generic, SampleRate = 10, ChannelCountOverride = 4,
            ChannelNamesOverride = ["Bx", "By", "Bz", "温度"], ChannelUnitsOverride = ["nT", "nT", "nT", "°C"],
        }, new ConnectionConfig());
        await storage.SaveReadingsAsync(Enumerable.Range(0, 20).Select(i => new MagnetometerReading
        {
            SessionId = withTemperature, Timestamp = t0.AddMilliseconds(i * 100), SensorType = SensorType.Generic,
            ChannelValues = [1 + i, 2, 3, 25 + i * .01],
        }));
        await storage.EndSessionAsync(withTemperature);

        var window = new MainWindow { DataContext = main, Width = 1440, Height = 920, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var errors = new StringWriter(); var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task Settle(string? shot = null)
        {
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            if (shot != null && !string.IsNullOrEmpty(shots)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(shots, $"data-{shot}.png"));
        }
        try
        {
            window.Show();
            main.CurrentPage = AppPage.Data;
            await sessions.RefreshSessionsCommand.ExecuteAsync(null);
            await Settle();
            var page = FindVisualChild<DataPageView>(window)!;
            var playbackChart = FindVisualChild<RealtimeChartView>(page)!;
            Assert.Equal(2, sessions.SessionsView.Cast<object>().Count());

            sessions.SelectedSession = sessions.Sessions.Single(s => s.Id == gradient);
            await Settle("detail");
            // 列表模式下回放层（含曲线和播放控制）必须隐藏，不能盖在会话详情上。
            Assert.False(main.DataPage.IsPlaybackOpen);
            Assert.False(playbackChart.IsVisible);
            Assert.Equal(6, main.DataPage.ExportChannels.Count);

            main.DataPage.ExportChannels[2].IsSelected = false;
            Assert.Equal("0,1,3,4,5", sessions.ExportChannelIndices);
            await sessions.RefreshSessionsCommand.ExecuteAsync(null);
            await Settle();
            // 刷新换成新对象后仍选中同一会话，导出勾选保留。
            Assert.Equal(gradient, sessions.SelectedSession?.Id);
            Assert.True(main.DataPage.HasSelection);
            Assert.False(main.DataPage.ExportChannels[2].IsSelected);
            Assert.Equal("0,1,3,4,5", sessions.ExportChannelIndices);

            await main.DataPage.OpenSelectedPlaybackCommand.ExecuteAsync(null);
            history.SeekTo(0.5);
            await Settle("playback");
            Assert.True(playbackChart.IsVisible);
            Assert.Equal(3000, history.TotalReadings);

            main.DataPage.BackToListCommand.Execute(null);
            await Settle();
            Assert.False(playbackChart.IsVisible);
            Assert.Equal(gradient, sessions.SelectedSession?.Id);

            sessions.SearchText = "不存在的名称";
            await Settle("search-empty");
            Assert.Empty(sessions.SessionsView.Cast<object>());
            Assert.Contains("没有符合", sessions.EmptyListHint);
            sessions.SearchText = "";
            // 结束日期按整天包含。
            sessions.FilterEndDate = DateTime.Today;
            Assert.Equal(2, sessions.SessionsView.Cast<object>().Count());
            sessions.FilterEndDate = DateTime.Today.AddDays(-1);
            Assert.Empty(sessions.SessionsView.Cast<object>());
            sessions.FilterEndDate = null;
            await Settle();
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
        }
        finally
        {
            window.DataContext = null; window.Close(); ortho.Cleanup();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            storage.Dispose();
            using (var c = new SqliteConnection(db.ConnectionString)) { c.Open(); SqliteConnection.ClearPool(c); }
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    });

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisualChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private static void SaveScreenshot(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
