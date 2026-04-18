using System.Windows.Controls;
using TurboSuite.Docs.ViewModels;

namespace TurboSuite.Docs.Views;

public partial class CountsTab : UserControl
{
    public CountsTab()
    {
        InitializeComponent();
    }

    private void NewExportRadio_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CountsViewModel vm)
            vm.IsUpdateMode = false;
    }
}
