using System.Windows;
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
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "退出前保存未完成：" + ex.Message, "数据尚未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        try
        {
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
    }

    private void ShowAbout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutDialog { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>
    /// 状态栏"有新版本"角标。用启动检查时缓存的结果直接开窗，不重新联网。
    /// </summary>
    private async void ShowUpdate_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = App.Services?.GetService<UpdateCoordinator>();
        if (coordinator?.LastKnownUpdate is not { } info)
        {
            // 缓存意外丢失时退回"关于"窗口，那里有手动检查按钮
            ShowAbout_Click(sender, e);
            return;
        }

        await coordinator.ShowUpdateDialogAsync(this, info);
    }

    private void RecordOrthoPoint_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.DataBus.RaiseManualOrthoRecord();
    }
}
