#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using TurboSuite.Name.Services;
using RevitColor = Autodesk.Revit.DB.Color;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;

namespace TurboSuite.Name.Views;

/// <summary>
/// Per-layer Line Graphics editor for TurboName's folded-in VG → Imported Categories table (TurboName-12) —
/// a faithful stand-in for Revit's native "Line Graphics" popup: Pattern / Color / Weight, with Clear Overrides
/// and OK/Cancel. Seeded from the layer's current <see cref="OverrideGraphicSettings"/> (cloned, so surface /
/// halftone overrides survive), it composes a new override on OK and exposes it via <see cref="Result"/>; the
/// caller writes it through the shared external event. All value-object work — no Revit API context needed here
/// (spike-confirmed). Color is picked with the native Windows <see cref="System.Windows.Forms.ColorDialog"/>,
/// pre-seeded with the firm template's three grayscale custom colors in the same bottom-right slots Revit's own
/// color dialog shows them in.
/// </summary>
public partial class LineGraphicsDialog : Window
{
    private const string NoOverride = "<No Override>";

    private readonly OverrideGraphicSettings _current;
    private RevitColor _color;      // working color; _hasColor gates whether it's an override
    private bool _hasColor;

    /// <summary>The composed override the caller applies. Null until OK / Clear Overrides.</summary>
    public OverrideGraphicSettings Result { get; private set; }

    public LineGraphicsDialog(string layerName, OverrideGraphicSettings current,
        IReadOnlyList<LinePatternOption> patternOptions)
    {
        InitializeComponent();
        _current = current ?? new OverrideGraphicSettings();
        Title = $"Line Graphics — {layerName}";

        // Pattern roster + seed from the current pattern id (falls back to <No Override>).
        PatternCombo.ItemsSource = patternOptions;
        var patId = _current.ProjectionLinePatternId ?? ElementId.InvalidElementId;
        PatternCombo.SelectedItem =
            patternOptions.FirstOrDefault(o => o.Id == patId) ?? patternOptions.FirstOrDefault();

        // Weight roster: <No Override> + 1..16, seeded from the current weight.
        var weights = new List<object> { NoOverride };
        for (int w = 1; w <= 16; w++) weights.Add(w);
        WeightCombo.ItemsSource = weights;
        int curWeight = _current.ProjectionLineWeight;
        WeightCombo.SelectedItem = curWeight >= 1 && curWeight <= 16 ? (object)curWeight : NoOverride;

        // Color: seed from the current override if it carries a valid one.
        var cc = _current.ProjectionLineColor;
        if (cc != null && cc.IsValid) { _color = cc; _hasColor = true; }
        UpdateColorDisplay();
    }

    private void UpdateColorDisplay()
    {
        if (_hasColor)
        {
            ColorSwatch.Background = new MediaBrush(MediaColor.FromRgb(_color.Red, _color.Green, _color.Blue));
            ColorLabel.Text = $"RGB {_color.Red}-{_color.Green}-{_color.Blue}";
        }
        else
        {
            ColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            ColorLabel.Text = NoOverride;
        }
    }

    // ── Custom-colors palette (mirrors the firm template's Revit color dialog) ──
    // The Windows color dialog's "Custom colors" panel is 16 slots laid out 2 rows × 8 columns, filled
    // left-to-right, top row first — so indices 13/14/15 are the bottom-right corner, where the shipped project
    // template puts the three common grayscales. Slots are packed BGR (0x00BBGGRR), NOT RGB.
    // Static so a color the user adds mid-session survives to the next open (each click builds a fresh dialog);
    // it resets with Revit, which is the same lifetime as the rest of this window's transient state.
    private static int[] _customColors = BuildCustomColors();

    private static int[] BuildCustomColors()
    {
        var slots = new int[16];
        for (int i = 0; i < slots.Length; i++) slots[i] = Win32Bgr(255, 255, 255); // unset reads as white
        slots[13] = Win32Bgr(221, 221, 221);
        slots[14] = Win32Bgr(187, 187, 187);
        slots[15] = Win32Bgr(102, 102, 102);
        return slots;
    }

    private static int Win32Bgr(int r, int g, int b) => r | (g << 8) | (b << 16);

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        var owner = new WindowInteropHelper(this).Handle;
        using var dlg = new AnchoredColorDialog(owner)
        {
            FullOpen = true,
            AnyColor = true,
            CustomColors = _customColors
        };
        if (_hasColor) dlg.Color = DrawingColor.FromArgb(_color.Red, _color.Green, _color.Blue);
        if (dlg.ShowDialog(new Win32Window(owner)) == System.Windows.Forms.DialogResult.OK)
        {
            _customColors = dlg.CustomColors; // keep anything the user added via "Add to Custom Colors"
            _color = new RevitColor(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            _hasColor = true;
            UpdateColorDisplay();
        }
    }

    // Clone the current override (keeps surface/halftone), then write the three Lines fields — set or clear.
    private OverrideGraphicSettings Compose(bool clear)
    {
        var ogs = new OverrideGraphicSettings(_current);

        if (clear || WeightCombo.SelectedItem is not int weight)
            ogs.SetProjectionLineWeight(-1);
        else
            ogs.SetProjectionLineWeight(weight);

        if (clear || !_hasColor)
            ogs.SetProjectionLineColor(RevitColor.InvalidColorValue);
        else
            ogs.SetProjectionLineColor(_color);

        var patId = clear ? ElementId.InvalidElementId
            : (PatternCombo.SelectedItem as LinePatternOption)?.Id ?? ElementId.InvalidElementId;
        ogs.SetProjectionLinePatternId(patId);

        return ogs;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Compose(clear: false);
        DialogResult = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Compose(clear: true);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Minimal <see cref="System.Windows.Forms.IWin32Window"/> so a WPF HWND can own a WinForms dialog.</summary>
    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    /// <summary>
    /// The native color picker, opened centred on this dialog instead of adrift on the screen.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Windows.Forms.ColorDialog"/> wraps Win32 <c>ChooseColor</c> and exposes no position
    /// API — and it does not merely *default* to somewhere unhelpful, it actively moves itself:
    /// <c>CommonDialog.HookProc</c> handles <c>WM_INITDIALOG</c> by calling <c>MoveToScreenCenter</c>, which
    /// places the dialog at one THIRD of the working area's height. That is the upper-middle-of-screen landing
    /// spot, on the monitor holding the mouse, no matter where the Line Graphics window actually is.
    ///
    /// Because the placement is done in the hook, an owner handle alone will not move it — the hook runs last
    /// and overrides whatever the owner implied. So the fix has to be the hook too: let the base class run
    /// (it also seeds the initial color and caption), then reposition. <c>WM_INITDIALOG</c> fires once the
    /// dialog exists at its final size but before it is painted, so there is no visible jump.
    ///
    /// The owner passed to <c>ShowDialog</c> is still worth setting, for the other half of the problem: an
    /// unowned picker is a sibling of the Line Graphics window and can fall behind it.
    ///
    /// Sizes come from <c>GetWindowRect</c> (device pixels) rather than WPF's DIP-based Left/Top/Width/Height,
    /// which would need per-monitor DPI correction to land right on a scaled display.
    /// </remarks>
    private sealed class AnchoredColorDialog : System.Windows.Forms.ColorDialog
    {
        private const int WM_INITDIALOG = 0x0110;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private readonly IntPtr _anchor;

        public AnchoredColorDialog(IntPtr anchor) => _anchor = anchor;

        protected override IntPtr HookProc(IntPtr hWnd, int msg, IntPtr wparam, IntPtr lparam)
        {
            var result = base.HookProc(hWnd, msg, wparam, lparam);
            if (msg == WM_INITDIALOG) CentreOnAnchor(hWnd);
            return result;
        }

        private void CentreOnAnchor(IntPtr hWnd)
        {
            if (_anchor == IntPtr.Zero) return;
            if (!GetWindowRect(_anchor, out RECT a) || !GetWindowRect(hWnd, out RECT d)) return;

            int w = d.Right - d.Left, h = d.Bottom - d.Top;
            int x = a.Left + ((a.Right - a.Left) - w) / 2;
            int y = a.Top + ((a.Bottom - a.Top) - h) / 2;

            // Clamp to the anchor's monitor work area. The picker is taller than the Line Graphics window, so
            // centring on a window near the top or bottom of the screen pushes its OK button off the edge —
            // and the failure mode is a modal dialog the user cannot dismiss with the mouse.
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(MonitorFromWindow(_anchor, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                x = Math.Max(mi.rcWork.Left, Math.Min(x, mi.rcWork.Right - w));
                y = Math.Max(mi.rcWork.Top, Math.Min(y, mi.rcWork.Bottom - h));
            }

            SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
