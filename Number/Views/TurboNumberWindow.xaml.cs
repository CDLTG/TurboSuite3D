#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TurboSuite.Number.ViewModels;

namespace TurboSuite.Number.Views
{
    public partial class TurboNumberWindow : Window
    {
        private int _dragFromIndex = -1;
        private Point _dragStartPoint;
        private ListBoxItem _lastHighlighted;

        public TurboNumberWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is NumberMainViewModel vm && vm.CircuitTab != null)
                vm.CircuitTab.RequestClose = () => this.Close();
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

        private void CircuitDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var vm = DataContext as NumberMainViewModel;
            if (vm?.CircuitTab == null) return;
            var dataGrid = (System.Windows.Controls.DataGrid)sender;
            vm.CircuitTab.SetSelectedRows(dataGrid.SelectedItems);
        }

        private void RoomOrderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move);
            _dragFromIndex = -1;
            ClearHighlight();
        }

        private void RoomOrderListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("RoomDragIndex"))
                return;

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

            var vm = (NumberMainViewModel)DataContext;
            vm.KeypadTab.MoveRoom(fromIndex, toIndex);
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
    }
}
