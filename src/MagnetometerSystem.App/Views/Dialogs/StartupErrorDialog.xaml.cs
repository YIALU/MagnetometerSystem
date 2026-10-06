using System.Windows;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class StartupErrorDialog : Window
{
    public StartupErrorDialog(Exception exception)
    {
        InitializeComponent();
        // 日志目录或日志初始化也可能失败，诊断信息不能依赖文件日志。
        DetailsText.Text = exception.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
