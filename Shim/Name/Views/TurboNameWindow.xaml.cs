#nullable disable
using System.Windows;
using TurboSuite.Name.ViewModels;

namespace TurboSuite.Name.Views;

public partial class TurboNameWindow : Window
{
    public TurboNameWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TurboNameViewModel vm)
            vm.CloseAction = result => { DialogResult = result; Close(); };
    }

    // Lazy CAD discovery: load layers/blocks/tags only when a discovery dropdown is first opened.
    private void OnDiscoveryDropDownOpened(object sender, System.EventArgs e)
    {
        if (DataContext is TurboNameViewModel vm)
            vm.CadConfig.EnsureDiscoveryLoaded();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
