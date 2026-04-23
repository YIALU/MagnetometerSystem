using System.Windows;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class SessionPickerDialog : Window
{
    private readonly IDataStorageService _storageService;

    public SessionInfo? SelectedSession { get; private set; }

    public SessionPickerDialog(IDataStorageService storageService)
    {
        _storageService = storageService;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "加载中...";
        try
        {
            var sessions = await _storageService.GetSessionsAsync();
            SessionsGrid.ItemsSource = sessions;
            StatusText.Text = $"共 {sessions.Count} 个会话";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"加载失败: {ex.Message}";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionInfo s)
        {
            MessageBox.Show("请选择一个会话", "提示");
            return;
        }
        SelectedSession = s;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionsGrid.SelectedItem is SessionInfo s)
        {
            SelectedSession = s;
            DialogResult = true;
        }
    }
}
