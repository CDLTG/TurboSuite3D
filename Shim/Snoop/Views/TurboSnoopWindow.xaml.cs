#nullable disable
using System.Windows;
using System.Windows.Input;

namespace TurboSuite.Snoop.Views
{
    /// <summary>
    /// Modeless TurboSnoop window — a read-only TreeView over one picked linked family's VG report. Pure
    /// display: the pick happened in the command before this opened, so the code-behind only closes the window.
    /// </summary>
    public partial class TurboSnoopWindow : Window
    {
        public TurboSnoopWindow()
        {
            InitializeComponent();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
