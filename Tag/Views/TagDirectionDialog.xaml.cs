using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TurboSuite.Tag.Models;

namespace TurboSuite.Tag.Views;

public partial class TagDirectionDialog : Window
{
    public TagDirection SelectedDirection { get; private set; } = TagDirection.None;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public TagDirectionDialog(bool includeLeftRight, IntPtr ownerHandle)
    {
        InitializeComponent();
        new WindowInteropHelper(this) { Owner = ownerHandle };

        if (!includeLeftRight)
        {
            RightButton.Visibility = Visibility.Collapsed;
            LeftButton.Visibility = Visibility.Collapsed;
        }

        Loaded += OnLoaded;
        KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape)
                DialogResult = false;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!GetCursorPos(out POINT pt))
            return;

        // Convert physical pixels to WPF device-independent units
        var source = PresentationSource.FromVisual(this);
        double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        double cursorX = pt.X * dpiScaleX;
        double cursorY = pt.Y * dpiScaleY;

        // Get work area of the monitor the cursor is on (in physical pixels)
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        double workLeft = SystemParameters.WorkArea.Left, workTop = SystemParameters.WorkArea.Top, workRight = SystemParameters.WorkArea.Right, workBottom = SystemParameters.WorkArea.Bottom;
        if (GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            workLeft = monitorInfo.rcWork.Left * dpiScaleX;
            workTop = monitorInfo.rcWork.Top * dpiScaleY;
            workRight = monitorInfo.rcWork.Right * dpiScaleX;
            workBottom = monitorInfo.rcWork.Bottom * dpiScaleY;
        }

        // Position so cursor lands between the Down and Left/Right buttons, offset slightly left
        double anchorX = ActualWidth * 2 / 3;
        double anchorY = ActualHeight / 2;

        // Use actual button positions if available
        var downPos = DownButton.TranslatePoint(new Point(0, DownButton.ActualHeight), this);
        var nextButton = LeftButton.Visibility == Visibility.Visible ? LeftButton : RightButton;
        var nextPos = nextButton.TranslatePoint(new Point(0, 0), this);
        anchorY = (downPos.Y + nextPos.Y) / 2;

        double x = cursorX - anchorX;
        double y = cursorY - anchorY;

        if (x + ActualWidth > workRight)
            x = workRight - ActualWidth;
        if (y + ActualHeight > workBottom)
            y = workBottom - ActualHeight;
        if (x < workLeft)
            x = workLeft;
        if (y < workTop)
            y = workTop;

        Left = x;
        Top = y;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        SelectedDirection = TagDirection.Up;
        DialogResult = true;
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        SelectedDirection = TagDirection.Down;
        DialogResult = true;
    }

    private void Right_Click(object sender, RoutedEventArgs e)
    {
        SelectedDirection = TagDirection.Right;
        DialogResult = true;
    }

    private void Left_Click(object sender, RoutedEventArgs e)
    {
        SelectedDirection = TagDirection.Left;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }
}
