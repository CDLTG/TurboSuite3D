#nullable disable
using System.Windows;

namespace TurboSuite.Name.Views;

public partial class TurboNameWindow : Window
{
    public TurboNameWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
