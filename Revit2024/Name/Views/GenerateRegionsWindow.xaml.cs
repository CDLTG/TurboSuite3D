#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TurboSuite.Name.Views;

public partial class GenerateRegionsWindow : Window
{
    public GenerateRegionsWindow()
    {
        InitializeComponent();
        Loaded += (s, e) => PositionInsideOwner();
    }

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
}
