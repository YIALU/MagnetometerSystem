using System.Windows;
using MagnetometerSystem.Core.Services;
using Serilog;

namespace MagnetometerSystem.App.Views.Dialogs;

/// <summary>
/// 新版本提示窗口：展示更新日志，负责下载安装包并拉起安装。
/// 任何一步失败都退化为"打开下载页面"，不把用户堵在这里。
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly IUpdateService _updateService;
    private UpdateInfo _info;
    private readonly IReadOnlyList<UpdateInfo> _sources;

    private CancellationTokenSource? _cts;
    private bool _isDownloading;
    private long _lastReportedBytes;
    private string _downloadSource = "";

    /// <summary>用户点了"跳过此版本"。由调用方负责写入偏好。</summary>
    public bool SkipRequested { get; private set; }

    public UpdateDialog(IUpdateService updateService, UpdateInfo info)
    {
        InitializeComponent();

        _updateService = updateService;
        _info = info;
        _sources = new[] { info }.Concat(info.Mirrors).ToArray();
        SourceSelector.ItemsSource = _sources;
        SourceSelector.SelectedItem = info;
        SourceSelector.IsEnabled = _sources.Count > 1;

        HeadlineText.Text = $"发现新版本 v{info.Version}";
        VersionSummaryText.Text = $"当前 v{AppVersion.Number}  →  最新 v{info.Version}";

        var published = info.PublishedAt is { } dt
            ? $"发布于 {dt.LocalDateTime:yyyy-MM-dd}"
            : null;

        PackageInfoText.Text = info.CanDownload
            ? string.Join("　　", new[] { published, $"当前使用{AppVersion.PackageKindDisplay}" }
                .Where(s => !string.IsNullOrEmpty(s)))
            : "暂时无法在软件内下载此版本，可以打开下载页面查看。";

        UpdateSummaryText.Text = GetUserSummary(info.ReleaseNotes);
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "（本次发布没有填写更新说明）"
            : info.ReleaseNotes.Trim();

        if (!info.CanDownload)
        {
            UpdateButton.Content = "打开下载页面";
        }
    }

    // ------------------------------------------------------------------ 按钮

    private void Source_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isDownloading || SourceSelector.SelectedItem is not UpdateInfo selected) return;
        _info = selected with { Mirrors = _sources.Where(x => x.Source != selected.Source).ToArray() };
        PackageInfoText.Text = _info.CanDownload
            ? $"当前使用{AppVersion.PackageKindDisplay}"
            : "暂时无法在软件内下载此版本，可以打开下载页面查看。";
        UpdateSummaryText.Text = GetUserSummary(_info.ReleaseNotes);
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(_info.ReleaseNotes) ? "（本次发布没有填写更新说明）" : _info.ReleaseNotes.Trim();
        UpdateButton.Content = _info.CanDownload ? "下载更新" : "打开下载页面";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!_info.CanDownload)
        {
            OpenReleasePageAndClose();
            return;
        }

        await DownloadAndApplyAsync();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        SkipRequested = true;
        Close();
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
        StatusText.Text = "正在取消…";
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e) => OpenReleasePageAndClose();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 下载中直接关窗则取消下载；服务会清掉半截的 .part 文件
        if (_isDownloading) _cts?.Cancel();
    }

    // ------------------------------------------------------------------ 下载

    private async Task DownloadAndApplyAsync()
    {
        MainWindow? preparedWindow = null;
        bool exitRequested = false;
        var failureMessage = "新版本未能下载。请检查网络连接和下载位置，重试或打开下载页面手动下载。";
        _cts = new CancellationTokenSource();
        SetDownloadingState(true);

        try
        {
            var progress = new Progress<DownloadProgress>(OnProgress);
            var localPath = await _updateService.DownloadAsync(_info, progress, _cts.Token);

            StatusText.Text = "下载完成，正在准备更新…";
            Log.Information("更新包已下载: {Path}", localPath);

            if (_updateService.Options.PackageKind == AppPackageKind.Installer)
            {
                var mainWindow = Application.Current.MainWindow as MainWindow
                    ?? throw new InvalidOperationException("无法找到主窗口，未启动安装。");
                failureMessage = "更新暂未开始：数据或设置未能全部保存。请检查保存位置和磁盘空间，在采集工作台重试保存后再更新。";
                StatusText.Text = "正在保存采集数据，完成后会关闭软件并开始安装…";
                await mainWindow.PrepareForExitAsync();
                preparedWindow = mainWindow;
                _cts.Token.ThrowIfCancellationRequested();
            }
            failureMessage = "更新包已下载，但未能开始更新。请重试，或打开下载页面按说明手动更新。";
            var shouldExit = _updateService.TryApplyUpdate(_info, localPath);

            if (shouldExit)
            {
                // 安装器可能立即关闭本进程；启动它之前已经等待全部保存。
                exitRequested = true;
                Application.Current.Shutdown();
                return;
            }

            // 便携版：已为用户打开压缩包所在文件夹
            MessageBox.Show(this,
                $"新版本已下载到：\n{localPath}\n\n" +
                "请关闭本程序后，把压缩包内的文件解压覆盖到当前程序目录即可完成升级。\n" +
                "更新前请确认采集数据已保存，并备份重要数据。",
                "下载完成", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (OperationCanceledException)
        {
            Log.Information("用户取消了更新下载");
            SetDownloadingState(false);
            StatusText.Text = "已取消下载。";
            ProgressPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载或安装更新失败");
            SetDownloadingState(false);
            ShowFailure(failureMessage);
        }
        finally
        {
            // 安装未启动、取消或便携模式不会退出；恢复正常操作。
            if (preparedWindow != null && !exitRequested)
                preparedWindow.CancelExitPreparation();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        if (p.BytesReceived == 0 || p.BytesReceived < _lastReportedBytes) _lastReportedBytes = 0;
        _downloadSource = p.Source?.ToString() ?? _info.SourceDisplay;
        // 每 80KB 一次回调，全量刷 UI 太浪费；节流到 256KB 或下载完成时刷新
        if (p.BytesReceived - _lastReportedBytes < 256 * 1024 &&
            p.BytesReceived != p.TotalBytes && p.BytesReceived != 0)
        {
            return;
        }
        _lastReportedBytes = p.BytesReceived;

        if (p.Percent is { } percent)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = percent;
            StatusText.Text = $"{_downloadSource} 正在下载… {percent:F0}%  " +
                              $"({FormatSize(p.BytesReceived)} / {FormatSize(p.TotalBytes!.Value)})";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            StatusText.Text = $"{_downloadSource} 正在下载… {FormatSize(p.BytesReceived)}";
        }
    }

    // ------------------------------------------------------------------ 辅助

    private void SetDownloadingState(bool downloading)
    {
        _isDownloading = downloading;
        SourceSelector.IsEnabled = !downloading && _sources.Count > 1;

        ProgressPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.IsEnabled = true;

        SkipButton.IsEnabled = !downloading;
        LaterButton.IsEnabled = !downloading;

        if (downloading)
        {
            OpenPageButton.Visibility = Visibility.Collapsed;
            _lastReportedBytes = 0;
            DownloadProgressBar.Value = 0;
            StatusText.Text = "正在连接…";
        }
    }

    private void ShowFailure(string message)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        StatusText.Text = message;

        // 自动下载走不通时，把手动下载的路给用户留出来
        UpdateButton.Content = "重试";
        OpenPageButton.Visibility = Visibility.Visible;
    }

    private void OpenReleasePageAndClose()
    {
        try
        {
            _updateService.OpenReleasePage(_info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开发行版页面失败");
            MessageBox.Show(this,
                $"无法打开浏览器，请手动访问：\n{_info.HtmlUrl}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private static string GetUserSummary(string notes)
    {
        const string startMarker = "<!-- user-notes:start -->";
        const string endMarker = "<!-- user-notes:end -->";
        var start = notes.IndexOf(startMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += startMarker.Length;
            var end = notes.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end > start && !string.IsNullOrWhiteSpace(notes[start..end]))
                return notes[start..end].Trim();
        }
        return "发布者未提供简要说明，您可以展开下方的完整更新说明，或打开下载页面了解详细变化。";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F1} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
