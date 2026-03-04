#nullable disable
using System.Windows;
using System.Windows.Input;

namespace TurboSuite.Zones.Views
{
    public partial class OptimizePreviewWindow : Window
    {
        public bool Confirmed { get; private set; }

        public OptimizePreviewWindow()
        {
            InitializeComponent();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
