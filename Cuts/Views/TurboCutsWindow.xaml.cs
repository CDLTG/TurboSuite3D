using System.Windows;
using System.Windows.Input;
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
}
