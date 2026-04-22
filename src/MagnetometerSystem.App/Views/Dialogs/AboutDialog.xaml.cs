using System.Windows;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        VersionText.Text = AppVersion.Display;
        VersionNumberText.Text = AppVersion.Number;
        CommitText.Text = AppVersion.Commit ?? "(未注入)";
        BuildTimeText.Text = AppVersion.BuildTime == DateTime.MinValue
            ? "(未知)"
            : AppVersion.BuildTime.ToString("yyyy-MM-dd HH:mm:ss");
        RuntimeText.Text = $".NET {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = $"磁力仪系统 {AppVersion.Display}\n" +
                   $"Commit: {AppVersion.Commit}\n" +
                   $"Build:  {AppVersion.BuildTime:yyyy-MM-dd HH:mm:ss}\n" +
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
