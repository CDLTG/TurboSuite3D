#nullable disable
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        CapContentToScreen();
        PositionInsideOwner();

        if (DataContext is TurboNameViewModel vm)
            vm.RequestClose += () => { _closingConfirmed = true; Close(); };
    }

    // The window sizes to its content and grows upward from the bottom-right anchor — there's no fixed content
    // height, because a hardcoded cap either wastes space on a tall display or clips one on a short one. This is
    // the only bound: never let the content region exceed what the screen can show, so the scroll bar returns on
    // a short display instead of the title bar running off the top. Reserve covers the title bar, the outer
    // margins, the Close row, and a little breathing room from the screen edges. Runs before PositionInsideOwner
    // so that method measures the final height.
    private void CapContentToScreen()
    {
        const double reserve = 110;
        double available = SystemParameters.WorkArea.Height - reserve;
        ContentScroll.MaxHeight = Math.Max(400, available);
        UpdateLayout();
    }

    // Pin the window's bottom-right corner to Revit's client bottom-right (clear of the scroll bars + status
    // bar), matching where the old solo Generate Regions window opened. The consolidated window is taller, so
    // anchoring the bottom-right keeps that corner constant while the extra height grows upward.
    private void PositionInsideOwner()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Owner != IntPtr.Zero &&
            GetClientRect(helper.Owner, out RECT clientRect))
        {
            // Map client bottom-right to screen coordinates (physical pixels)
            var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };
            ClientToScreen(helper.Owner, ref bottomRight);

            // Get DPI of the monitor the Revit window is on
            double dpi = GetDpiForWindow(helper.Owner);
            double scale = 96.0 / dpi; // physical pixels → WPF DIPs

            // Offset to clear Revit's scroll bars and status bar
            double scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
            double scrollBarHeight = SystemParameters.HorizontalScrollBarHeight;
            double statusBarHeight = 26; // Revit status bar approximate height in DIPs
            double margin = 4;

            Left = bottomRight.X * scale - ActualWidth - scrollBarWidth - margin;
            Top = bottomRight.Y * scale - ActualHeight - scrollBarHeight - statusBarHeight - margin;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - SystemParameters.VerticalScrollBarWidth - 4;
            Top = area.Bottom - ActualHeight - SystemParameters.HorizontalScrollBarHeight - 28;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
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

    // Re-read the project's FilledRegionTypes each time the Region Type dropdown opens, so a type added
    // mid-session shows up without reopening the window.
    private void RegionTypeCombo_DropDownOpened(object sender, System.EventArgs e)
    {
        if (DataContext is TurboNameViewModel vm)
            vm.CadConfig.RefreshRegionTypeNames();
    }

    // Both block-mode tag dropdowns act as action menus: picking an attribute assigns it (Room Name appends to
    // the ordered chips; Ceiling Height replaces its single value), then the combo resets to no-selection — the
    // chosen tag leaves the shared candidate list anyway. The guard suppresses the re-fire that reset triggers.
    private bool _assigningTag;

    private void RoomNameAddCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => AssignFromCombo(sender, (vm, tag) => vm.CadConfig.AddRoomNameTag(tag));

    private void CeilingHeightCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => AssignFromCombo(sender, (vm, tag) => vm.CadConfig.CeilingHeightTag = tag);

    private void HeightBlockTagCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => AssignFromCombo(sender, (vm, tag) => vm.CadConfig.CeilingHeightBlockTag = tag);

    private void AssignFromCombo(object sender, System.Action<TurboNameViewModel, string> assign)
    {
        if (_assigningTag) return;
        if (sender is System.Windows.Controls.ComboBox combo && combo.SelectedItem is string tag
            && DataContext is TurboNameViewModel vm)
        {
            // Guard first: assigning removes the tag from this combo's ItemsSource, re-firing SelectionChanged —
            // suppress that (and the explicit reset) so we never assign twice.
            _assigningTag = true;
            assign(vm, tag);
            combo.SelectedIndex = -1;
            _assigningTag = false;
        }
    }

    // ListBox.SelectedItems can't be bound in XAML (same limitation as DataGrid — see CLAUDE.md), so push the
    // multi-selection to the ViewModel, which decides whether a row edit hits one layer or all of them.
    private void LayerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox list && DataContext is TurboNameViewModel vm)
            vm.SetSelectedLayers(list.SelectedItems.OfType<CadLayerRowViewModel>());
    }

    // Per-layer Line Graphics editor (TurboName-12): opens the native-style Pattern/Color/Weight dialog seeded
    // from the row's cached override, and on OK applies the composed override through the shared external event.
    // With a multi-selection the same override lands on every selected layer, so the title says how many.
    private void LineGraphicsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn
            && btn.DataContext is CadLayerRowViewModel row
            && DataContext is TurboNameViewModel vm)
        {
            int count = vm.LineGraphicsTargetCount(row);
            string label = count > 1 ? $"{count} layers" : row.LayerName;
            var dlg = new LineGraphicsDialog(label, row.LineOverride, vm.LinePatternOptions) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
                vm.ApplyLineGraphics(row, dlg.Result);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
