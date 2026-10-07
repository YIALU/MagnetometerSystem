using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class DataPageView : UserControl
{
    public DataPageView() => InitializeComponent();

    private HistoryPlaybackViewModel? Playback => (DataContext as DataPageViewModel)?.Playback;

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => Playback?.OnSeekDragStarted();

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider slider) Playback?.OnSeekDragCompleted(slider.Value);
    }
}
