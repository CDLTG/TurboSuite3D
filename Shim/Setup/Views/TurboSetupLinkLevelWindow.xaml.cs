#nullable disable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TurboSuite.Setup.ViewModels;

namespace TurboSuite.Setup.Views;

public partial class TurboSetupLinkLevelWindow : Window
{
    public TurboSetupLinkLevelWindow()
    {
        InitializeComponent();
    }

    public TurboSetupLinkLevelWindow(TurboSetupLinkLevelViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += () =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Single-click toggle for the Include checkbox and Main radio cells (matches TurboDocs).
    private void DataGridCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || cell.IsEditing) return;

        var toggle = FindVisualChild<ToggleButton>(cell);
        if (toggle == null) return;

        // Checkboxes flip; radios (Main) only ever turn on.
        toggle.IsChecked = toggle is RadioButton || !(toggle.IsChecked ?? false);
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        e.Handled = true;
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
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
