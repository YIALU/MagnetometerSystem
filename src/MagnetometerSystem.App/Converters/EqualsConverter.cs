using System.Globalization;
using System.Windows.Data;

namespace MagnetometerSystem.App.Converters;

/// <summary>
/// 多值转换器：比较两个值是否相等，返回 bool
/// </summary>
public class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        return values[0] != null && values[0].Equals(values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
