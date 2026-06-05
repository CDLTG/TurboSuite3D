using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Documents;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Document = Autodesk.Revit.DB.Document;
using Xceed.Wpf.AvalonDock;
using Xceed.Wpf.AvalonDock.Controls;
using Xceed.Wpf.AvalonDock.Layout;

namespace TurboSuite.Tab;

public static class TabColoringService
{
    private static readonly object UpdateLock = new();
    private static DockingManager? _dockingManager;
    private static UIApplication? _uiApp;
    private static string _lastStateHash = "";
    private static bool _isRunning;

    // Color slots: docId → (paletteIndex, isFamily). Persists across updates so
    // a project keeps its color even when tabs are opened/closed.
    private static readonly Dictionary<long, (int ColorIndex, bool IsFamily)> _docSlots = new();
    private static int _nextColorIndex;

    // Cache of original tab styles, saved before first modification so we can restore them.
    private static readonly Dictionary<TabItem, Style?> _originalStyles = new();

    private static readonly Color[] Palette =
    [
        Color.FromRgb(66, 133, 244),   // Blue
        Color.FromRgb(34, 139, 60),    // Green (darkened for white text contrast)
        Color.FromRgb(251, 188, 4),    // Yellow
        Color.FromRgb(234, 67, 53),    // Red
        Color.FromRgb(0, 172, 193),    // Teal
        Color.FromRgb(171, 71, 188),   // Purple
        Color.FromRgb(255, 112, 67),   // Deep Orange
        Color.FromRgb(124, 179, 66),   // Light Green
        Color.FromRgb(3, 155, 229),    // Light Blue
        Color.FromRgb(216, 27, 96),    // Pink
    ];

    public static bool IsRunning => _isRunning;

    public static bool Start(IntPtr mainWindowHandle, UIApplication uiApp)
    {
        if (_isRunning) return true;

        var rootVisual = GetRootVisual(mainWindowHandle);
        if (rootVisual == null) return false;

        _dockingManager = FindFirstChild<DockingManager>(rootVisual);
        if (_dockingManager == null) return false;

        _uiApp = uiApp;
        _dockingManager.LayoutUpdated += OnLayoutUpdated;
        _isRunning = true;

        UpdateTabColors();
        return true;
    }

    public static void Stop()
    {
        if (!_isRunning) return;

        if (_dockingManager != null)
        {
            _dockingManager.LayoutUpdated -= OnLayoutUpdated;
            RestoreTabStyles();
            _dockingManager = null;
        }

        _uiApp = null;
        _lastStateHash = "";
        _docSlots.Clear();
        _originalStyles.Clear();
        _nextColorIndex = 0;
        _isRunning = false;
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateTabColors();
    }

    private static void UpdateTabColors()
    {
        lock (UpdateLock)
        {
            if (_dockingManager == null || _uiApp == null) return;

            try
            {
                var tabItems = GetDocumentTabItems();
                if (tabItems.Count == 0) return;

                var hash = string.Join("|", tabItems.Select(t => t.GetHashCode()));
                if (hash == _lastStateHash) return;
                _lastStateHash = hash;

                // Build API-side document map: mfcDocId → isFamily.
                var apiDocs = BuildApiDocumentMap();

                // Prune slots for documents that are no longer open.
                var activeDocIds = new HashSet<long>(apiDocs.Keys);
                var staleIds = _docSlots.Keys.Where(id => !activeDocIds.Contains(id)).ToList();
                foreach (var id in staleIds)
                    _docSlots.Remove(id);

                // Prune cached original styles for tabs that no longer exist.
                var staleTabs = _originalStyles.Keys.Where(t => !tabItems.Contains(t)).ToList();
                foreach (var tab in staleTabs)
                    _originalStyles.Remove(tab);

                // Apply styles to each tab.
                foreach (var tab in tabItems)
                {
                    long tabDocId = GetTabDocumentId(tab);
                    if (tabDocId == 0) continue;

                    // Cache original style before first modification.
                    if (!_originalStyles.ContainsKey(tab))
                        _originalStyles[tab] = tab.Style;

                    // Determine or assign a color slot for this document.
                    if (!_docSlots.TryGetValue(tabDocId, out var slot))
                    {
                        bool isFamily = apiDocs.TryGetValue(tabDocId, out var fam) && fam;
                        slot = (_nextColorIndex % Palette.Length, isFamily);
                        _docSlots[tabDocId] = slot;
                        _nextColorIndex++;
                    }

                    var color = Palette[slot.ColorIndex];
                    var baseStyle = _originalStyles.GetValueOrDefault(tab);
                    tab.Style = slot.IsFamily
                        ? CreateFamilyTabStyle(color, baseStyle)
                        : CreateProjectTabStyle(color, baseStyle);
                }
            }
            catch
            {
                // Silently ignore — UI manipulation should never crash Revit.
            }
        }
    }

    /// <summary>
    /// Builds a map of MFC document pointer → IsFamilyDocument for all open documents.
    /// </summary>
    private static Dictionary<long, bool> BuildApiDocumentMap()
    {
        var map = new Dictionary<long, bool>();
        if (_uiApp == null) return map;

        foreach (Document doc in _uiApp.Application.Documents)
        {
            if (doc.IsLinked) continue;
            try
            {
                long docId = GetApiDocumentId(doc);
                if (docId != 0)
                    map[docId] = doc.IsFamilyDocument;
            }
            catch
            {
                // Skip documents where reflection fails.
            }
        }
        return map;
    }

    /// <summary>
    /// Gets the MFC document pointer from a Revit Document via reflection.
    /// </summary>
    private static long GetApiDocumentId(Document doc)
    {
        var getMfcDoc = doc.GetType().GetMethod("getMFCDoc",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (getMfcDoc == null) return 0;

        var mfcDoc = getMfcDoc.Invoke(doc, []);
        if (mfcDoc == null) return 0;

        var getPointer = mfcDoc.GetType().GetMethod("GetPointerValue",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (getPointer == null) return 0;

        var ptr = (IntPtr?)getPointer.Invoke(mfcDoc, []);
        return ptr?.ToInt64() ?? 0;
    }

    /// <summary>
    /// Gets the MFC document pointer from a TabItem via the AvalonDock content chain.
    /// TabItem.Content → LayoutDocument.Content → MFCMDIChildFrameControl.Content
    /// → MFCMDIFrameHost.document (IntPtr).
    /// </summary>
    private static long GetTabDocumentId(TabItem tab)
    {
        try
        {
            if (tab.Content is not LayoutDocument layoutDoc) return 0;
            if (layoutDoc.Content is not ContentControl childFrame) return 0;
            if (childFrame.Content is not FrameworkElement frameHost) return 0;

            // MFCMDIFrameHost has a 'document' field (IntPtr).
            var docField = frameHost.GetType().GetProperty("document",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (docField != null)
                return ((IntPtr?)docField.GetValue(frameHost))?.ToInt64() ?? 0;

            // Fallback: try field instead of property.
            var docFieldInfo = frameHost.GetType().GetField("document",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (docFieldInfo != null)
                return ((IntPtr?)docFieldInfo.GetValue(frameHost))?.ToInt64() ?? 0;

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void RestoreTabStyles()
    {
        try
        {
            foreach (var (tab, origStyle) in _originalStyles)
                tab.Style = origStyle;
            _lastStateHash = "";
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static List<TabItem> GetDocumentTabItems()
    {
        var result = new List<TabItem>();
        if (_dockingManager == null) return result;

        foreach (var pane in FindVisualChildren<LayoutDocumentPaneControl>(_dockingManager))
        {
            result.AddRange(FindVisualChildren<TabItem>(pane));
        }
        return result;
    }

    /// <summary>
    /// Project tabs: full background fill with auto-contrast text.
    /// </summary>
    private static Style CreateProjectTabStyle(Color background, Style? baseStyle)
    {
        var foreground = GetContrastForeground(background);
        var selectedBg = LightenOrDarken(background, 0.15);
        var hoverBg = LightenOrDarken(background, 0.08);

        var style = new Style(typeof(TabItem), baseStyle);
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(background)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(foreground)));
        style.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(foreground)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(background)));

        var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(selectedBg)));
        selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(selectedBg)));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(foreground)));
        selectedTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(foreground)));
        style.Triggers.Add(selectedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(hoverBg)));
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(hoverBg)));
        hoverTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(foreground)));
        style.Triggers.Add(hoverTrigger);

        style.Seal();
        return style;
    }

    /// <summary>
    /// Family tabs: colored top bar only, default background/foreground preserved.
    /// </summary>
    private static Style CreateFamilyTabStyle(Color barColor, Style? baseStyle)
    {
        var style = new Style(typeof(TabItem), baseStyle);
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(barColor)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 3, 0, 0)));

        var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(barColor)));
        selectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 3, 0, 0)));
        style.Triggers.Add(selectedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(barColor)));
        style.Triggers.Add(hoverTrigger);

        style.Seal();
        return style;
    }

    private static Color GetContrastForeground(Color bg)
    {
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance > 0.5 ? Colors.Black : Colors.White;
    }

    private static Color LightenOrDarken(Color color, double amount)
    {
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        if (luminance < 0.5)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, color.R + 255 * amount),
                (byte)Math.Min(255, color.G + 255 * amount),
                (byte)Math.Min(255, color.B + 255 * amount));
        }
        return Color.FromRgb(
            (byte)Math.Max(0, color.R - 255 * amount),
            (byte)Math.Max(0, color.G - 255 * amount),
            (byte)Math.Max(0, color.B - 255 * amount));
    }

    private static DependencyObject? GetRootVisual(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;
            var hwndSource = HwndSource.FromHwnd(hwnd);
            return hwndSource?.RootVisual as DependencyObject;
        }
        catch
        {
            return null;
        }
    }

    private static T? FindFirstChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindFirstChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
