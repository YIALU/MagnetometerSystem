using System.Windows;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MagnetometerSystem.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"磁力仪数据采集与分析系统  —  {AppVersion.Display}";
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
