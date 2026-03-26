using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class HistoryPlaybackView : UserControl
{
    public HistoryPlaybackView()
    {
        InitializeComponent();
    }

    private HistoryPlaybackViewModel? ViewModel => DataContext as HistoryPlaybackViewModel;

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        ViewModel?.OnSeekDragStarted();
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel?.OnSeekDragCompleted(slider.Value);
        }
    }
}
