#nullable disable
using System.Windows;
using System.Windows.Input;

namespace TurboSuite.Dmx.Views
{
    /// <summary>
    /// Modeless TurboDMX window (Phase 1): the declarations panel + always-on bill bound to
    /// <c>DmxMainViewModel</c>. Modeless so Revit reads (the Refresh re-read) run through an
    /// <c>IExternalEventHandler</c> work queue (TurboNumber/TurboZones pattern) rather than blocking the UI.
    /// Code-behind is intentionally thin — all behavior lives in the ViewModel.
    /// </summary>
    public partial class TurboDmxWindow : Window
    {
        public TurboDmxWindow()
        {
            InitializeComponent();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
