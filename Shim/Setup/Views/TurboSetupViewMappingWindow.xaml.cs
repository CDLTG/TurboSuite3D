#nullable disable
using System.Windows;
using TurboSuite.Setup.ViewModels;

namespace TurboSuite.Setup.Views;

public partial class TurboSetupViewMappingWindow : Window
{
    public TurboSetupViewMappingWindow()
    {
        InitializeComponent();
    }

    public TurboSetupViewMappingWindow(TurboSetupViewMappingViewModel vm) : this()
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
}
