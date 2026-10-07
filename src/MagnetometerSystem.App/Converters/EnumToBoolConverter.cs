using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MagnetometerSystem.App.Converters;

/// <summary>枚举值等于参数名时为 true；用于导航栏和分段按钮的单选绑定。</summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Enum e && parameter is string name && string.Equals(e.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(Nullable.GetUnderlyingType(targetType) ?? targetType, name) : Binding.DoNothing;
}

/// <summary>数值不为 0 时可见；用于“异常 N”等只在有内容时出现的提示。</summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IConvertible c && System.Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>值相等（按字符串比较）时为 true；ConvertBack 返回参数，用于数值型分段按钮。</summary>
public class ValueEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter != null && string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter != null ? System.Convert.ChangeType(parameter.ToString(), Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture)! : Binding.DoNothing;
}

/// <summary>null / 空字符串时折叠。</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>布尔取反（双向）。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>时间窗口秒数的显示文字：0 表示全部。</summary>
public class SecondsLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        double d when d <= 0 => "全部",
        double d when d >= 60 && d % 60 == 0 => $"{d / 60:0} min",
        double d => $"{d:0.##} s",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>值等于参数时可见（用于按页签切换内容区）。</summary>
public class ValueEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter != null && string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>数值为 0 时可见；用于列表为空时的提示。</summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IConvertible c && System.Convert.ToDouble(c, CultureInfo.InvariantCulture) == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>两个值相等时可见（多值绑定），例如列表中标出正在录制的会话。</summary>
public class EqualsToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] != null && values[0].Equals(values[1]) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null / 空字符串时可见。</summary>
public class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>参数 "A,B" 表示值为其中之一时可见；"!A" 表示值不是 A 时可见。</summary>
public class EnumInSetToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = parameter?.ToString() ?? "";
        var negate = text.StartsWith('!');
        var names = text.TrimStart('!').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hit = value != null && names.Contains(value.ToString());
        return hit != negate ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>向导步骤条：values = [当前步骤, 本步骤编号]，返回 "done" / "on" / "todo"。</summary>
public class StepStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not int current
            || !int.TryParse(System.Convert.ToString(values[1], CultureInfo.InvariantCulture), out var step)) return "todo";
        return current > step ? "done" : current == step ? "on" : "todo";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>串口校验位的中文名。</summary>
public class ParityNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "None" => "无",
        "Odd" => "奇",
        "Even" => "偶",
        "Mark" => "Mark",
        "Space" => "Space",
        var other => other ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
