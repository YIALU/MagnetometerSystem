using System.Globalization;
using System.Windows.Data;

namespace MagnetometerSystem.App.Converters;

public class ColumnCountToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int columnCount && columnCount > 0)
        {
            return $"{100.0 / columnCount}*";
        }
        return "1*";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
