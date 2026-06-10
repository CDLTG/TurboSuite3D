#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
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
                Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Push the clicked row into the VM so the detail pane + "Select in Project" track it.
        // SelectionUnit is full-row here (no in-cell editing), so SelectedItem binds cleanly.
        private void CircuitsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is DataGrid grid
                && DataContext is RpsMainViewModel vm
                && grid.SelectedItem is RpsCircuitRowViewModel row)
            {
                vm.SelectedRow = row;
            }
        }
    }

    /// <summary>null → Visible (placeholder), non-null → Collapsed.</summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value == null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>non-null → Visible (detail content), null → Collapsed.</summary>
    public class NotNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value != null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>feet (double) → "F' - I"" string for the detail-pane fixtures table.</summary>
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

    /// <summary>SubDriverAssignment → "Sub-driver N (Driver M): xW / yW" header line.</summary>
    public class SubDriverHeaderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is SubDriverAssignment sub)
                return $"Sub-driver {sub.SubDriverIndex} (Driver {sub.DriverIndex}): {sub.TotalLoad:F1}W / {sub.Capacity:F0}W";
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>FixtureSegment → "TypeMark (label): wattage / length" detail line.</summary>
    public class SegmentDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is FixtureSegment seg)
            {
                string label = seg.TypeMark ?? "";
                if (seg.IsSplit && !string.IsNullOrEmpty(seg.SplitLabel))
                    label += $" ({seg.SplitLabel})";
                if (seg.LinearLength <= 0.0001)
                    return $"{label}: {seg.Wattage:F1}W";
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
