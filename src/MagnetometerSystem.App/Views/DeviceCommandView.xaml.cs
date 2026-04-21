using System.Windows.Controls;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class DeviceCommandView : UserControl
{
    public DeviceCommandView()
    {
        InitializeComponent();
        LogTextBox.TextChanged += OnLogTextChanged;
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is DeviceCommandViewModel vm && vm.PauseAutoScroll) return;
        LogScrollViewer.ScrollToBottom();
    }
}
