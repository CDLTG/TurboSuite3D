using System.Windows.Controls;

namespace TurboSuite.Docs.Views;

public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncRadioButtons();
    }

    private void SyncRadioButtons()
    {
        if (DataContext is TurboSuite.Docs.ViewModels.DocsViewModel vm && !vm.UseLargeFormat)
            SmallFormatRadio.IsChecked = true;
    }
}
