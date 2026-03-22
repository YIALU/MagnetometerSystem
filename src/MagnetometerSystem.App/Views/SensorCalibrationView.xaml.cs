using System.Windows.Controls;

namespace MagnetometerSystem.App.Views;

public partial class SensorCalibrationView : UserControl
{
    public SensorCalibrationView()
    {
        InitializeComponent();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.SensorCalibrationViewModel vm && vm.SelectedProfile != null)
        {
            vm.LoadSelectedProfileCommand.Execute(null);
        }
    }
}
