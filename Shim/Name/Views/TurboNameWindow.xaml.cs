#nullable disable
using System.ComponentModel;
using System.Windows;
using TurboSuite.Name.ViewModels;

namespace TurboSuite.Name.Views;

public partial class TurboNameWindow : Window
{
    private bool _closingConfirmed;

    public TurboNameWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TurboNameViewModel vm)
            vm.RequestClose += () => { _closingConfirmed = true; Close(); };
    }

    /// <summary>Close immediately, skipping the on-close save round-trip. Used by the doc-close guard — during
    /// DocumentClosing we must not raise the shared external event against a closing document.</summary>
    public void ForceClose()
    {
        _closingConfirmed = true;
        Close();
    }

    // Modeless close flow: give the VM a chance to flush a pending save (via the shared external event) before
    // the window tears down. TryClose returns false to cancel this close; it re-requests the close once the
    // save has committed (RequestClose → _closingConfirmed → allow).
    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (DataContext is TurboNameViewModel vm && !vm.TryClose())
            e.Cancel = true;
    }

    // Lazy CAD discovery: load layers/blocks/tags only when a discovery dropdown is first opened.
    private void OnDiscoveryDropDownOpened(object sender, System.EventArgs e)
    {
        if (DataContext is TurboNameViewModel vm)
            vm.CadConfig.EnsureDiscoveryLoaded();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
