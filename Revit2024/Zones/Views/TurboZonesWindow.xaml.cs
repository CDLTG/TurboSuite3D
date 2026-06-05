#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TurboSuite.Zones.ViewModels;

namespace TurboSuite.Zones.Views
{
    public partial class TurboZonesWindow : Window
    {
        public TurboZonesWindow()
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

        private void LoadNamesGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            if (sender is not DataGrid grid) return;

            grid.BeginEdit();

            // Mirror the current cell's row into the VM so "Select in Project" knows which
            // circuit to target. Cell-selection mode lets us preserve in-cell editing on the
            // other columns, so we sync from CurrentCell rather than binding SelectedItem.
            if (DataContext is ZonesMainViewModel vm
                && grid.CurrentCell.Item is ZonesCircuitViewModel row)
            {
                vm.LoadNameTab.SelectedRow = row;
            }
        }

        private void LoadNamesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not DataGrid grid)
                return;

            e.Handled = true;

            // Commit the current edit
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);

            // Move to the next row in the same column
            var currentCell = grid.CurrentCell;
            int currentIndex = grid.Items.IndexOf(currentCell.Item);
            if (currentIndex < 0 || currentIndex >= grid.Items.Count - 1)
                return;

            var nextItem = grid.Items[currentIndex + 1];
            grid.CurrentCell = new DataGridCellInfo(nextItem, currentCell.Column);
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(grid.CurrentCell);
            grid.ScrollIntoView(nextItem, currentCell.Column);
        }
    }

    public class DimmingTypeToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush ELVBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xAD, 0xD6));
        private static readonly SolidColorBrush ZeroTenBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x70));
        private static readonly SolidColorBrush RelayBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0xA0));
        private static readonly SolidColorBrush UnknownBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

        // Color is keyed off the actual module part number so a 0-10V module used for Relay
        // (LQSE-4T5) still reads as orange, while a dedicated switching module (LQSE-4S8) is green.
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string pn = (value as string)?.ToUpperInvariant() ?? "";

            // 0-10V / universal-dimming modules
            if (pn.Contains("4T5") || pn.Contains("DIMFLV") || pn.Contains("0-10V"))
                return ZeroTenBrush;

            // Switching / relay modules
            if (pn.Contains("4S8") || pn.Contains("HSW") || pn.Equals("RELAY"))
                return RelayBrush;

            // ELV / adaptive modules
            if (pn.Contains("4A5") || pn.Contains("DIMU") || pn.Equals("ELV"))
                return ELVBrush;

            return UnknownBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EmptySlotsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IntToRangeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
                return Enumerable.Range(0, count).Cast<object>().ToArray();
            return Array.Empty<object>();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PercentToWidthConverter : IValueConverter
    {
        private const double MaxWidth = 164.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percent)
                return Math.Max(0, Math.Min(percent, 1.0)) * MaxWidth;
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToRedForegroundConverter : IValueConverter
    {
        private static readonly System.Windows.Media.SolidColorBrush RedBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x00, 0x00));
        private static readonly System.Windows.Media.SolidColorBrush DefaultBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? RedBrush : DefaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
