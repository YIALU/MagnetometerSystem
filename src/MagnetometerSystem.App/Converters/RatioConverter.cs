using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MagnetometerSystem.App.Converters;

/// <summary>尺寸按比例换算：value × parameter（如 0.55）。用于让面板最高占容器的一部分。</summary>
public class RatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double size && double.IsFinite(size) && size > 0
            && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return size * ratio;
        return double.PositiveInfinity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
