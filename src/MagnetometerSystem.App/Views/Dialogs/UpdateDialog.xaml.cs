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
    private readonly UpdateInfo _info;

    private CancellationTokenSource? _cts;
    private bool _isDownloading;
    private long _lastReportedBytes;

    /// <summary>用户点了"跳过此版本"。由调用方负责写入偏好。</summary>
    public bool SkipRequested { get; private set; }

    public UpdateDialog(IUpdateService updateService, UpdateInfo info)
    {
        InitializeComponent();

        _updateService = updateService;
        _info = info;

        HeadlineText.Text = $"发现新版本 v{info.Version}";
        VersionSummaryText.Text = $"当前 v{AppVersion.Number}  →  最新 v{info.Version}";

        var published = info.PublishedAt is { } dt
            ? $"发布于 {dt.LocalDateTime:yyyy-MM-dd}"
            : null;

        PackageInfoText.Text = info.CanDownload
            ? string.Join("　　", new[] { published, $"当前为{AppVersion.PackageKindDisplay}", info.FileName }
                .Where(s => !string.IsNullOrEmpty(s)))
            : "该版本未提供与当前安装方式匹配的安装包，请到发行版页面手动下载。";

        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "（本次发布没有填写更新说明）"
            : info.ReleaseNotes.Trim();

        if (!info.CanDownload)
        {
            UpdateButton.Content = "打开下载页面";
        }
    }

    // ------------------------------------------------------------------ 按钮

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
        _cts = new CancellationTokenSource();
        SetDownloadingState(true);

        try
        {
            var progress = new Progress<DownloadProgress>(OnProgress);
            var localPath = await _updateService.DownloadAsync(_info, progress, _cts.Token);

            StatusText.Text = "下载完成，正在启动安装…";
            Log.Information("更新包已下载: {Path}", localPath);

            var shouldExit = _updateService.TryApplyUpdate(_info, localPath);

            if (shouldExit)
            {
                // 安装版：静默安装会关闭本程序并在装完后自动重启新版本
                Application.Current.Shutdown();
                return;
            }

            // 便携版：已为用户打开压缩包所在文件夹
            MessageBox.Show(this,
                $"新版本已下载到：\n{localPath}\n\n" +
                "请关闭本程序后，把压缩包内的文件解压覆盖到当前程序目录即可完成升级。\n" +
                "测量数据保存在用户目录，不会因覆盖而丢失。",
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
            ShowFailure(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        // 每 80KB 一次回调，全量刷 UI 太浪费；节流到 256KB 或下载完成时刷新
        if (p.BytesReceived - _lastReportedBytes < 256 * 1024 &&
            p.BytesReceived != p.TotalBytes)
        {
            return;
        }
        _lastReportedBytes = p.BytesReceived;

        if (p.Percent is { } percent)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = percent;
            StatusText.Text = $"正在下载… {percent:F0}%  " +
                              $"({FormatSize(p.BytesReceived)} / {FormatSize(p.TotalBytes!.Value)})";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            StatusText.Text = $"正在下载… {FormatSize(p.BytesReceived)}";
        }
    }

    // ------------------------------------------------------------------ 辅助

    private void SetDownloadingState(bool downloading)
    {
        _isDownloading = downloading;

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
        StatusText.Text = $"更新失败：{message}";

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
                $"无法打开浏览器，请手动访问：\n{_updateService.Options.ReleasesUrl}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F1} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
