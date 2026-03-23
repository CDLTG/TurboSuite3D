using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TurboSuite.Cuts.ViewModels;

namespace TurboSuite.Cuts.Views;

public partial class TurboCutsWindow : Window
{
    public TurboCutsWindow()
    {
        InitializeComponent();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TurboCutsViewModel vm)
            vm.SaveSettings();
        Close();
    }

    private void DataGridCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || cell.IsEditing || cell.IsReadOnly) return;

        // Find the CheckBox inside the cell and toggle it directly
        var checkBox = FindVisualChild<CheckBox>(cell);
        if (checkBox != null)
        {
            checkBox.IsChecked = !checkBox.IsChecked;
            // Push the binding update
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
