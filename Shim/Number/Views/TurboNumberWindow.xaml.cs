#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TurboSuite.Number.ViewModels;

namespace TurboSuite.Number.Views
{
    public partial class TurboNumberWindow : Window
    {
        private int _dragFromIndex = -1;
        private Point _dragStartPoint;
        private ListBoxItem _lastHighlighted;

        // Auto-scroll while dragging a room near the top/bottom edge of the sidebar list, so a
        // room can be dragged across a list taller than the viewport (e.g. #70 → #5) in one gesture.
        private ScrollViewer _dragScrollViewer;
        private DispatcherTimer _dragScrollTimer;
        private int _dragScrollDirection; // -1 = up, +1 = down, 0 = none

        public TurboNumberWindow()
        {
            InitializeComponent();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as NumberMainViewModel;
            if (vm?.RoomOrder == null) return;
            vm.RoomOrder.SearchText = string.Empty;
            // Return focus to the box so the next room name can be typed immediately.
            RoomSearchBox.Focus();
        }

        private void CircuitDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var vm = DataContext as NumberMainViewModel;
            if (vm?.CircuitTab == null) return;
            var dataGrid = (System.Windows.Controls.DataGrid)sender;
            vm.CircuitTab.SetSelectedRows(dataGrid.SelectedItems);
        }

        private void RoomOrderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as NumberMainViewModel;
            if (vm?.RoomOrder == null) return;

            // Let scrollbar clicks pass through
            if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>((DependencyObject)e.OriginalSource) != null)
                return;

            // In reorder mode, handle click-to-order instead of drag
            if (vm.RoomOrder.IsReordering)
            {
                _dragFromIndex = -1;
                var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (listBoxItem?.DataContext is RoomOrderItem roomItem)
                    vm.RoomOrder.ToggleRoomClick(roomItem);
                e.Handled = true;
                return;
            }

            _dragStartPoint = e.GetPosition(null);
            var listBox = (ListBox)sender;
            var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
            _dragFromIndex = item != null ? listBox.ItemContainerGenerator.IndexFromContainer(item) : -1;
        }

        private void RoomOrderListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragFromIndex < 0)
                return;

            var pos = e.GetPosition(null);
            var diff = pos - _dragStartPoint;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var listBox = (ListBox)sender;
            var data = new DataObject("RoomDragIndex", _dragFromIndex);
            StartDragScroll(listBox);
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move); // blocks until drop completes
            StopDragScroll();
            _dragFromIndex = -1;
            ClearHighlight();
        }

        private void RoomOrderListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("RoomDragIndex"))
                return;

            UpdateDragScroll((ListBox)sender, e.GetPosition((ListBox)sender));

            var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
            if (target == _lastHighlighted)
                return;

            ClearHighlight();

            if (target != null)
            {
                target.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A));
                target.BorderThickness = new Thickness(0, 2, 0, 0);
                _lastHighlighted = target;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void RoomOrderListBox_DragLeave(object sender, DragEventArgs e)
        {
            ClearHighlight();
        }

        private void RoomOrderListBox_Drop(object sender, DragEventArgs e)
        {
            ClearHighlight();

            if (!e.Data.GetDataPresent("RoomDragIndex"))
                return;

            int fromIndex = (int)e.Data.GetData("RoomDragIndex");
            var listBox = (ListBox)sender;
            var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
            int toIndex = target != null ? listBox.ItemContainerGenerator.IndexFromContainer(target) : listBox.Items.Count - 1;

            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
                return;

            // ObservableCollection.Move removes at fromIndex first (shifting items up),
            // then inserts at toIndex. When dragging down, the target item's index is
            // one higher than where the item will actually land after the remove, so
            // subtract 1 to match the visual "insert above this item" indicator.
            if (fromIndex < toIndex)
                toIndex--;

            if (fromIndex == toIndex)
                return;

            var vm = (NumberMainViewModel)DataContext;
            vm.RoomOrder.MoveRoom(fromIndex, toIndex);
        }

        // ── Drag auto-scroll ───────────────────────────────────────────────────────────────────────
        // DragDrop.DoDragDrop runs a nested message loop that pumps the Dispatcher, so a DispatcherTimer
        // keeps ticking during the drag. DragOver sets the direction from the cursor's edge proximity;
        // the timer scrolls one line per tick while the cursor rests near an edge (held still or moving).

        private void StartDragScroll(ListBox listBox)
        {
            _dragScrollViewer = FindDescendant<ScrollViewer>(listBox);
            _dragScrollDirection = 0;
            if (_dragScrollViewer == null) return;
            if (_dragScrollTimer == null)
            {
                _dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _dragScrollTimer.Tick += DragScrollTick;
            }
            _dragScrollTimer.Start();
        }

        private void StopDragScroll()
        {
            _dragScrollTimer?.Stop();
            _dragScrollDirection = 0;
            _dragScrollViewer = null;
        }

        private void UpdateDragScroll(ListBox listBox, Point pos)
        {
            if (_dragScrollViewer == null) return;
            const double edge = 28;
            if (pos.Y < edge)
                _dragScrollDirection = -1;
            else if (pos.Y > listBox.ActualHeight - edge)
                _dragScrollDirection = 1;
            else
                _dragScrollDirection = 0;
        }

        private void DragScrollTick(object sender, EventArgs e)
        {
            if (_dragScrollViewer == null || _dragScrollDirection == 0) return;
            if (_dragScrollDirection < 0)
                _dragScrollViewer.LineUp();
            else
                _dragScrollViewer.LineDown();
        }

        private void ClearHighlight()
        {
            if (_lastHighlighted != null)
            {
                _lastHighlighted.BorderBrush = null;
                _lastHighlighted.BorderThickness = new Thickness(0);
                _lastHighlighted = null;
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                var result = FindDescendant<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        // ── Location field: relock on re-picking the seed value from the dropdown ──────────────────
        // The override field is transient when unlocked: committing the seed value (or a blank) re-locks
        // (RoomOrderItem.ApplyUserLocation). The combo's TwoWay/LostFocus binding commits an actual edit
        // (typing a different value, or clearing it) on blur, but selecting the already-shown value from
        // the dropdown is not an "edit", so it would otherwise leave the row stuck unlocked. This handler
        // force-commits the current text on dropdown close — re-locking when it equals the seed, and
        // overriding when it differs.

        private void LocationCombo_DropDownClosed(object sender, EventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is RoomOrderItem item)
                item.LocationEntry = cb.Text;
        }
    }
}
