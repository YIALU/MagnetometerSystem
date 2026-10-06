using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.Views.Dialogs;

namespace MagnetometerSystem.App.Tests;

public class StartupErrorUiTests
{
    [Fact]
    public Task StartupFailureRetainsSelectableDetailsWithoutFileLogger() => WpfTestHost.RunAsync(async () =>
    {
        Exception failure;
        try { throw new InvalidOperationException("日志初始化失败", new UnauthorizedAccessException("目录不可写")); }
        catch (Exception ex) { failure = ex; }

        var dialog = new StartupErrorDialog(failure)
        {
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            dialog.Show();
            await WpfTestHost.PumpAsync();
            var expander = (Expander)dialog.FindName("DetailsExpander");
            var details = (TextBox)dialog.FindName("DetailsText");
            Assert.False(expander.IsExpanded);
            Assert.Equal(failure.ToString(), details.Text);
            Assert.Contains(nameof(UnauthorizedAccessException), details.Text);
            Assert.Contains(nameof(StartupFailureRetainsSelectableDetailsWithoutFileLogger), details.Text);
            expander.IsExpanded = true;
            await WpfTestHost.PumpAsync();
            Assert.True(details.IsVisible);
            Assert.True(details.IsReadOnly);
            details.SelectAll();
            Assert.Equal(failure.ToString(), details.SelectedText);
            UpdateSourceUiTests.SaveScreenshot((FrameworkElement)dialog.Content, "startup-error-details.png");
        }
        finally { dialog.Close(); }
    });
}
