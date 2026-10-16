#nullable enable
using System;
using System.Collections;
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

        // ── Drag-to-reorder: a loop's member zones AND a zone's circuit order ───────────────────────────────
        //  Two nested reorderable lists share these handlers. The zone order within a loop is the OUTER
        //  addressing key; a zone's circuit order is the within-zone key the Manual override sets. Both are a
        //  Move on the bound collection, which the respective VM persists off CollectionChanged(Move).
        //
        //  The handlers are list-agnostic: the drag payload is the row's DataContext as a plain object, and the
        //  target collection is resolved from the NEAREST ancestor ItemsControl's ItemsSource (as IList). This
        //  matters because the circuit ItemsControl nests inside the zone row — a type-specific finder walking
        //  up from a circuit row would wrongly match the outer zone collection. A drop is valid only when the
        //  dragged item and the row under the cursor are two distinct members of that same nearest collection,
        //  which keeps a reorder within one list (a zone can't be dropped into the circuit list, or vice versa).

        private Point _dragStartPoint;
        private object? _dragItem;
        private bool _isDragging;

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
            // A press that lands on a row button (← return-to-pool) is a click, not a drag — leave it alone.
            if (IsWithinButton(e.OriginalSource as DependencyObject))
            {
                _dragItem = null;
                return;
            }
            _dragStartPoint = e.GetPosition(null);
            _dragItem = (sender as FrameworkElement)?.DataContext;
        }

        private void ZoneRow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging || _dragItem == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isDragging = true;
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, _dragItem, DragDropEffects.Move);
            }
            finally
            {
                _isDragging = false;
                _dragItem = null;
            }
        }

        private void ZoneRow_DragOver(object sender, DragEventArgs e)
        {
            bool ok = CanDrop(sender, out _, out _);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetInsertionMark(sender, ok);
            e.Handled = true;
        }

        private void ZoneRow_DragLeave(object sender, DragEventArgs e) => SetInsertionMark(sender, false);

        private void ZoneRow_Drop(object sender, DragEventArgs e)
        {
            SetInsertionMark(sender, false);
            if (!CanDrop(sender, out var coll, out var target)) return;

            int from = coll!.IndexOf(_dragItem);
            int to = coll.IndexOf(target);
            if (from < 0 || to < 0) return;

            // The top-border cue means "insert above this row". ObservableCollection.Move removes-then-inserts,
            // so on a downward drag the item lands one slot low unless we decrement — the same correction
            // TurboNumber's room-order reorder applies.
            if (from < to) to--;
            if (from == to) return;

            MoveInList(coll, from, to);   // ObservableCollection.Move → CollectionChanged(Move) → VM persists
            e.Handled = true;
        }

        /// <summary>The drop is valid when the dragged item and the row under the cursor are two distinct
        /// members of the same nearest-ItemsControl collection — a within-one-list reorder.</summary>
        private bool CanDrop(object sender, out IList? coll, out object? target)
        {
            coll = null;
            target = (sender as FrameworkElement)?.DataContext;
            var dragged = _dragItem;
            if (dragged == null || target == null || ReferenceEquals(dragged, target))
                return false;

            coll = FindNearestItemsSource(sender as DependencyObject);
            return coll != null && coll.Contains(dragged) && coll.Contains(target);
        }

        /// <summary>Toggle the row's top border as an insertion-point cue while a drag hovers it.</summary>
        private static void SetInsertionMark(object sender, bool on)
        {
            if (sender is FrameworkElement fe && fe.Parent is Border b)
                b.BorderBrush = on ? InsertionBrush : Brushes.Transparent;
        }

        /// <summary>The nearest ancestor ItemsControl's ItemsSource as an <see cref="IList"/> — the list the
        /// hovered row belongs to (the inner circuit list for a circuit row, the member-zone list for a zone
        /// row). Both are ObservableCollections, so <see cref="MoveInList"/> can Move within either.</summary>
        private static IList? FindNearestItemsSource(DependencyObject? start)
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ItemsControl ic && ic.ItemsSource is IList list)
                    return list;
            return null;
        }

        /// <summary>Call <c>ObservableCollection&lt;T&gt;.Move(from, to)</c> on an untyped <see cref="IList"/>.
        /// IList has no Move, and a Remove+Insert would raise Remove/Add instead of Move — which the VMs' Move
        /// handlers (persist-on-reorder) would miss — so invoke the real Move by reflection.</summary>
        private static void MoveInList(IList coll, int from, int to)
        {
            var move = coll.GetType().GetMethod("Move", new[] { typeof(int), typeof(int) });
            if (move != null) move.Invoke(coll, new object[] { from, to });
            else { var item = coll[from]; coll.RemoveAt(from); coll.Insert(to, item); }  // defensive fallback
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
