using System.Windows;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;

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

    private void RecordOrthoPoint_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.DataBus.RaiseManualOrthoRecord();
    }
}
