using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views;

public partial class OrthogonalityCalibrationView : UserControl
{
    public OrthogonalityCalibrationView()
    {
        InitializeComponent();
    }

    private void ContinuousMode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OrthogonalityCalibrationViewModel vm)
            vm.SelectedMode = CalibrationCollectionMode.Continuous;
    }

    private void Manual48Mode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OrthogonalityCalibrationViewModel vm)
            vm.SelectedMode = CalibrationCollectionMode.Manual48;
    }
}
