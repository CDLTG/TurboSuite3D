using System.Windows;
using System.Windows.Input;
using TurboSuite.Docs.ViewModels;

namespace TurboSuite.Docs.Views;

public partial class TurboDocsWindow : Window
{
    public TurboDocsWindow()
    {
        InitializeComponent();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is DocsViewModel vm)
                vm.SaveSettings();
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DocsViewModel vm)
            vm.SaveSettings();
        Close();
    }
}
