using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class AboutDialog : Window
{
    private readonly UpdateCoordinator? _updateCoordinator;

    public AboutDialog()
    {
        InitializeComponent();

        VersionText.Text = AppVersion.Display;
        VersionNumberText.Text = AppVersion.Number;
        CommitText.Text = AppVersion.Commit ?? "(未注入)";
        BuildTimeText.Text = AppVersion.BuildTime == DateTime.MinValue
            ? "(未知)"
            : AppVersion.BuildTime.ToString("yyyy-MM-dd HH:mm:ss");
        PackageKindText.Text = AppVersion.PackageKindDisplay;
        RuntimeText.Text = $".NET {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

        // DI 尚未就绪（例如启动早期异常时弹窗）也要能显示版本信息，所以不强制解析
        _updateCoordinator = App.Services?.GetService<UpdateCoordinator>();

        var homepage = _updateCoordinator?.Service.Options.HomepageUrl
                       ?? "https://gitee.com/yialu/MagnetometerSystem";
        HomepageText.Text = homepage;
        HomepageLink.NavigateUri = new Uri(homepage);

        CheckUpdateButton.IsEnabled = _updateCoordinator is not null;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var url = e.Uri.ToString();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开项目主页失败");
            MessageBox.Show(this, $"无法打开浏览器，请手动访问：\n{url}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCoordinator is null) return;

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            var result = await _updateCoordinator.CheckManuallyAsync();

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Info is not null:
                    UpdateStatusText.Text = $"发现新版本 v{result.Info.Version}";
                    await _updateCoordinator.ShowUpdateDialogAsync(this, result.Info);
                    break;

                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = $"当前已是最新版本 v{AppVersion.Number}。";
                    break;

                default:
                    Log.Warning("手动检查更新失败: {Message}", result.ErrorMessage);
                    UpdateStatusText.Text = $"检查失败：{result.ErrorMessage}";
                    ShowManualDownloadFallback();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "手动检查更新出错");
            UpdateStatusText.Text = $"检查失败：{ex.Message}";
            ShowManualDownloadFallback();
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>检查不通时，问用户要不要直接去发行版页面看。</summary>
    private void ShowManualDownloadFallback()
    {
        if (_updateCoordinator is null) return;

        var answer = MessageBox.Show(this,
            "无法连接到更新服务器，请检查网络连接。\n\n是否打开发行版页面手动查看？",
            "检查更新失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                _updateCoordinator.Service.OpenReleasePage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开发行版页面失败");
            }
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = $"磁力仪系统 {AppVersion.Display}\n" +
                   $"Commit: {AppVersion.Commit}\n" +
                   $"Build:  {AppVersion.BuildTime:yyyy-MM-dd HH:mm:ss}\n" +
                   $"安装方式: {AppVersion.PackageKindDisplay}\n" +
                   $"Runtime: .NET {Environment.Version}";
        try
        {
            Clipboard.SetText(text);
            MessageBox.Show("已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败: {ex.Message}", "错误");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
