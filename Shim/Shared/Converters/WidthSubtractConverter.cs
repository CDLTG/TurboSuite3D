using System;
using System.Globalization;
using System.Windows.Data;

namespace TurboSuite.Shared.Converters;

/// <summary>
/// Returns an incoming width (a double, e.g. an ancestor's ActualWidth) minus the pixel amount in
/// ConverterParameter, floored at zero. Used to cap a wrapping TextBlock's MaxWidth to the viewport
/// width less the TreeView's expander/indent gutter — a WPF TreeViewItem's default header column is
/// Auto-width, so it measures at the text's full desired width and never wraps without an explicit cap.
/// </summary>
public class WidthSubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && !double.IsNaN(width) && !double.IsInfinity(width))
        {
            double subtract = 0;
            if (parameter is string s)
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out subtract);
            return Math.Max(0, width - subtract);
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
