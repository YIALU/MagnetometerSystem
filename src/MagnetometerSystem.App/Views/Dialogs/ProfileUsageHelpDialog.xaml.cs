using System.Windows;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class ProfileUsageHelpDialog : Window
{
    public ProfileUsageHelpDialog() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
