using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TurboSuite.Docs.Views;

public partial class ScheduleTab : UserControl
{
    public ScheduleTab()
    {
        InitializeComponent();
    }

    private void DataGridCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || cell.IsEditing || cell.IsReadOnly) return;

        var checkBox = FindVisualChild<CheckBox>(cell);
        if (checkBox != null)
        {
            checkBox.IsChecked = !checkBox.IsChecked;
            var binding = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty);
            binding?.UpdateSource();
            e.Handled = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
