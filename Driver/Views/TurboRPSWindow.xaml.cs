#nullable disable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.ViewModels;

namespace TurboSuite.Driver.Views
{
    public partial class TurboRPSWindow : Window
    {
        public TurboRPSWindow()
        {
            InitializeComponent();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void FixtureList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            };
            ((UIElement)((FrameworkElement)sender).Parent).RaiseEvent(eventArg);
        }
    }

    public class FlattenDevicesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is System.Collections.ObjectModel.ObservableCollection<DeviceGroupViewModel> groups)
            {
                return groups.SelectMany(g => g.Devices).ToList();
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FeetInchesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double feet)
            {
                if (feet <= 0.0001)
                    return "N/A";
                int wholeFeet = (int)feet;
                int remainingInches = (int)Math.Round((feet - wholeFeet) * 12.0);
                if (remainingInches >= 12) { wholeFeet++; remainingInches = 0; }
                return $"{wholeFeet}' - {remainingInches}\"";
            }
            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FamilyTypeDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is string name && values[1] is Autodesk.Revit.DB.FamilySymbol symbol)
            {
                string catalogNumber = "";
                string manufacturer = "";

                try
                {
                    var catParam = symbol.LookupParameter("Catalog Number1");
                    catalogNumber = catParam?.AsString() ?? "";

                    var mfgParam = symbol.LookupParameter("Manufacturer");
                    manufacturer = mfgParam?.AsString() ?? "";
                }
                catch { }

                return $"{catalogNumber} | {manufacturer}";
            }
            return values[0]?.ToString() ?? "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SubDriverHeaderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is SubDriverAssignment sub)
            {
                return $"Sub-driver {sub.SubDriverIndex} (Driver {sub.DriverIndex}): {sub.TotalLoad:F1}W / {sub.Capacity:F0}W";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IsRecommendedTypeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length >= 2
                && values[0] is Autodesk.Revit.DB.FamilySymbol symbol
                && values[1] is Autodesk.Revit.DB.ElementId recommendedId)
            {
                return symbol.Id == recommendedId;
            }
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SegmentDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is FixtureSegment seg)
            {
                string label = seg.TypeMark ?? "";
                if (seg.IsSplit && !string.IsNullOrEmpty(seg.SplitLabel))
                {
                    label += $" ({seg.SplitLabel})";
                }
                if (seg.LinearLength <= 0.0001)
                {
                    return $"{label}: {seg.Wattage:F1}W";
                }
                int wholeFeet = (int)seg.LinearLength;
                int remainingInches = (int)Math.Round((seg.LinearLength - wholeFeet) * 12.0);
                if (remainingInches >= 12) { wholeFeet++; remainingInches = 0; }
                string lengthStr = $"{wholeFeet}' - {remainingInches}\"";
                return $"{label}: {seg.Wattage:F1}W / {lengthStr}";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
