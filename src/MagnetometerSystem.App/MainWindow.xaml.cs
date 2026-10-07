using System.Windows;
using Serilog;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using MagnetometerSystem.Infrastructure.Configuration;

namespace MagnetometerSystem.App;

public partial class MainWindow : Window
{
    private bool _closeReady;
    private bool _closing;
    private readonly IAppConfigService? _exitConfigService;
    public MainWindow() : this(null) { }

    public MainWindow(IAppConfigService? exitConfigService)
    {
        _exitConfigService = exitConfigService;
        InitializeComponent();
        Title = $"磁力仪数据采集与分析系统  —  {AppVersion.Display}";
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_closeReady)
        {
            vm.OrthoCalibVM.Cleanup();
            vm.HistoryPlaybackVM.Dispose();
            vm.DeviceCommandVM.Dispose();
            vm.RealtimeChartVM.Dispose();
            return;
        }
        e.Cancel = true;
        if (_closing) return;
        try
        {
            await PrepareForExitAsync();
            // 即使保存同步完成，也必须等本次 Closing 事件返回后再关闭。
            await Dispatcher.InvokeAsync(Close);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出前保存数据或设置失败");
            MessageBox.Show(this,
                "数据或设置未能全部保存，暂时无法关闭。\n\n" +
                "请检查磁盘空间和保存位置是否可写。如果采集工作台提示保存失败，请先点击重试保存，再重新关闭。\n\n" +
                "请保留此窗口，避免强制结束程序。详细原因已记录在日志中。",
                "请先完成保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>保存成功后暂时封锁操作，直到退出或安装失败后明确恢复。</summary>
    public async Task PrepareForExitAsync()
    {
        if (_closeReady) return;
        if (_closing) throw new InvalidOperationException("正在准备退出，请等待保存完成。");
        if (DataContext is not MainViewModel vm) throw new InvalidOperationException("主窗口尚未就绪。");
        _closing = true;
        IsEnabled = false;
        ExitProgressOverlay.Visibility = Visibility.Visible;
        try
        {
            foreach (var feedback in Application.Current.Windows.OfType<FeedbackDialog>())
                await feedback.SaveDraftAsync();
            await App.PrepareForExitAsync(vm, _exitConfigService);
            _closeReady = true;
        }
        catch
        {
            CancelExitPreparation();
            throw;
        }
    }

    public void CancelExitPreparation()
    {
        _closeReady = false;
        _closing = false;
        IsEnabled = true;
        ExitProgressOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowFeedback_Click(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<FeedbackCoordinator>().Show(this);

    /// <summary>导航栏底部版本号：有更新时直接打开更新窗口（用启动检查缓存，不重新联网），否则打开“关于”。</summary>
    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = App.Services?.GetService<UpdateCoordinator>();
        if (DataContext is MainViewModel { HasUpdate: true } && coordinator?.LastKnownUpdate is { } info)
        {
            await coordinator.ShowUpdateDialogAsync(this, info);
            return;
        }
        new AboutDialog { Owner = this }.ShowDialog();
    }
}
