using System.Windows;

namespace TurboSuiteInstaller;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new InstallerViewModel();
    }
}
