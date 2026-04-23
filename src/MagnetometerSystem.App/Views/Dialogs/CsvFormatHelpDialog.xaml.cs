using System.Windows;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class CsvFormatHelpDialog : Window
{
    public CsvFormatHelpDialog() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
