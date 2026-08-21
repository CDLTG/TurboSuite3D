#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TurboSuite.Dali.ViewModels;

namespace TurboSuite.Dali.Views
{
    /// <summary>
    /// The standalone <b>TurboDALI</b> modeless window — the DALI loop-declaration UI plus the addressing
    /// surface (Write addresses, numbering lock, REVIEW list) and the live zone color overlay, dressed in the
    /// DMX chrome (blue header + roll-up, footer bar). DataContext is a <c>DaliMainViewModel</c> (which wraps
    /// the loop-declaration <c>DaliTab</c>), collected + shown by <c>DaliCommand</c>.
    /// </summary>
    public partial class TurboDaliWindow : Window
    {
        public TurboDaliWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ── Drag-to-reorder a loop's member zones ─────────────────────────────────────────────────────────
        //  The zone order within a loop is the OUTER addressing key (DaliAddressReconciler walks loop.ZoneNames
        //  in declared order), so letting the designer reorder in place beats remove-and-re-add-in-order. Pure
        //  view mechanics — the reorder is a Move on the bound Zones collection, and the VM persists it off the
        //  CollectionChanged Move. Restricted to same-loop reorder (both endpoints in the target row's list).

        private Point _dragStartPoint;
        private DaliZoneItemViewModel? _dragZone;
        private bool _isDraggingZone;

        /// <summary>Insertion-line color — the same TurboSuite header blue TurboNumber's room reorder uses, so
        /// the two drag surfaces read alike (and it's theme-stable, unlike the OS highlight brush).</summary>
        private static readonly Brush InsertionBrush = CreateInsertionBrush();

        private static Brush CreateInsertionBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A));
            brush.Freeze();
            return brush;
        }

        private void ZoneRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // A press that lands on the ← (return-to-pool) button is a click, not a drag — leave it alone.
            if (IsWithinButton(e.OriginalSource as DependencyObject))
            {
                _dragZone = null;
                return;
            }
            _dragStartPoint = e.GetPosition(null);
            _dragZone = (sender as FrameworkElement)?.DataContext as DaliZoneItemViewModel;
        }

        private void ZoneRow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingZone || _dragZone == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isDraggingZone = true;
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, _dragZone, DragDropEffects.Move);
            }
            finally
            {
                _isDraggingZone = false;
                _dragZone = null;
            }
        }

        private void ZoneRow_DragOver(object sender, DragEventArgs e)
        {
            bool ok = CanDrop(sender, e, out _, out _);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetInsertionMark(sender, ok);
            e.Handled = true;
        }

        private void ZoneRow_DragLeave(object sender, DragEventArgs e) => SetInsertionMark(sender, false);

        private void ZoneRow_Drop(object sender, DragEventArgs e)
        {
            SetInsertionMark(sender, false);
            if (!CanDrop(sender, e, out var coll, out var target)) return;

            var dragged = e.Data.GetData(typeof(DaliZoneItemViewModel)) as DaliZoneItemViewModel;
            int from = coll!.IndexOf(dragged!);
            int to = coll.IndexOf(target!);
            if (from < 0 || to < 0) return;

            // The top-border cue means "insert above this row". ObservableCollection.Move removes-then-inserts,
            // so on a downward drag the item lands one slot low unless we decrement — the same correction
            // TurboNumber's room-order reorder applies.
            if (from < to) to--;
            if (from == to) return;

            coll.Move(from, to);   // fires CollectionChanged(Move) → the VM recomputes + persists the new order
            e.Handled = true;
        }

        /// <summary>The drop is valid when the dragged zone and the row under the cursor are two distinct
        /// members of the same loop's Zones collection — same-loop reorder only.</summary>
        private static bool CanDrop(object sender, DragEventArgs e,
                                    out ObservableCollection<DaliZoneItemViewModel>? coll,
                                    out DaliZoneItemViewModel? target)
        {
            coll = null;
            target = (sender as FrameworkElement)?.DataContext as DaliZoneItemViewModel;
            var dragged = e.Data.GetData(typeof(DaliZoneItemViewModel)) as DaliZoneItemViewModel;
            if (dragged == null || target == null || ReferenceEquals(dragged, target))
                return false;

            coll = FindZonesCollection(sender as DependencyObject);
            return coll != null && coll.Contains(dragged) && coll.Contains(target);
        }

        /// <summary>Toggle the row's top border as an insertion-point cue while a drag hovers it.</summary>
        private static void SetInsertionMark(object sender, bool on)
        {
            if (sender is FrameworkElement fe && fe.Parent is Border b)
                b.BorderBrush = on ? InsertionBrush : Brushes.Transparent;
        }

        /// <summary>Nearest ancestor ItemsControl whose source is a zone collection — the inner member-zone
        /// list. The outer Loops ItemsControl is skipped by the element-type match on the generic collection.</summary>
        private static ObservableCollection<DaliZoneItemViewModel>? FindZonesCollection(DependencyObject? start)
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ItemsControl ic && ic.ItemsSource is ObservableCollection<DaliZoneItemViewModel> zones)
                    return zones;
            return null;
        }

        private static bool IsWithinButton(DependencyObject? source)
        {
            for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is ButtonBase) return true;
                if (d is Grid) return false;   // reached the row root without hitting a button
            }
            return false;
        }
    }

    /// <summary>bool → Visibility, inverted (true ⇒ Collapsed) — the placeholder/content flip. Local twin of
    /// the TurboZones window's converter; TurboSuiteStyles only ships the non-inverted BoolToVisibility.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v != Visibility.Visible;
    }
}
