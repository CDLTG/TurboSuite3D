using System.Windows;
using TurboSuite.App.ViewModels;

namespace TurboSuite.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CloseAction = result =>
            {
                DialogResult = result;
                Close();
            };
        }
    }

    // Lazy CAD discovery: load layers/blocks/tags only when a discovery dropdown is first opened.
    private void OnDiscoveryDropDownOpened(object sender, System.EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.EnsureDiscoveryLoaded();
    }
}
