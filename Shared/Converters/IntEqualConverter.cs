using System;
using System.Globalization;
using System.Windows.Data;

namespace TurboSuite.Shared.Converters;

/// <summary>
/// Two-way converter: binds an int property to a RadioButton.
/// ConverterParameter is the int value this radio represents.
/// Returns true when the bound value equals the parameter.
/// On ConvertBack, returns the parameter value when the radio is checked.
/// </summary>
public class IntEqualConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramValue))
            return intValue == paramValue;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr && int.TryParse(paramStr, out int paramValue))
            return paramValue;
        return Binding.DoNothing;
    }
}
