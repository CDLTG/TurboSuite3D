#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class KeypadTabViewModel : TabViewModelBase
    {
        private readonly ISwitchIdWriter _switchIdWriter;
        private readonly IRoomOrderStore _roomOrderStore;
        private readonly RoomOrderViewModel _roomOrder;
        private bool _isKeypadRoomSorted;

        /// <summary>
        /// When on, the keypad grid is sorted by the project-wide room order (custom sort,
        /// column sorting locked) instead of Value-then-Mark. Persisted per project via the
        /// TurboNumber-local store; renamed from the old "sidebar visible" flag now that the
        /// room-order sidebar is permanent — the underlying bool schema is reused verbatim.
        /// </summary>
        public bool IsKeypadRoomSorted
        {
            get => _isKeypadRoomSorted;
            set
            {
                if (SetProperty(ref _isKeypadRoomSorted, value))
                    OnPropertyChanged(nameof(CanUserSortColumns));
            }
        }

        public bool CanUserSortColumns => !_isKeypadRoomSorted;

        public ICommand ToggleKeypadSortCommand { get; }

        public KeypadTabViewModel(IReadOnlyList<NumberableRowViewModel> rows,
            RoomOrderViewModel roomOrder, bool keypadRoomSorted,
            IRevitWorkQueue workQueue, ISwitchIdWriter switchIdWriter, IRoomOrderStore roomOrderStore,
            IDeviceSelector selector)
            : base("Keypads", workQueue, selector)
        {
            _switchIdWriter = switchIdWriter;
            _roomOrderStore = roomOrderStore;
            _roomOrder = roomOrder;
            ToggleKeypadSortCommand = new RelayCommand(ToggleKeypadSort);

            foreach (var row in rows)
                AddRow(row);

            // The shared room-order editor owns the list + gestures; the keypad grid only
            // re-sorts when that committed order changes.
            _roomOrder.OrderChanged += OnRoomOrderChanged;

            _isKeypadRoomSorted = keypadRoomSorted;
            if (_isKeypadRoomSorted)
                ApplyCustomSort();
            else
                ApplyDefaultSort();
        }

        private void OnRoomOrderChanged()
        {
            if (_isKeypadRoomSorted)
                ApplyCustomSort();
        }

        private void ToggleKeypadSort()
        {
            IsKeypadRoomSorted = !IsKeypadRoomSorted;
            if (IsKeypadRoomSorted)
                ApplyCustomSort();
            else
                ApplyDefaultSort();

            var isSorted = IsKeypadRoomSorted;
            _workQueue.Enqueue(() => { _roomOrderStore.SaveKeypadRoomSorted(isSorted); return null; }, null);
        }

        private void ApplyCustomSort()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
            view.SortDescriptions.Clear();
            view.CustomSort = new RoomOrderComparer(_roomOrder.RoomOrder);
        }

        protected override void Apply()
        {
            _workQueue.Enqueue(() => { _switchIdWriter.WriteSwitchIds(Rows); return null; }, null);
        }

        private class RoomOrderComparer : IComparer
        {
            private readonly Dictionary<string, int> _orderMap;

            public RoomOrderComparer(ObservableCollection<RoomOrderItem> order)
            {
                _orderMap = new Dictionary<string, int>(order.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < order.Count; i++)
                    _orderMap[order[i].Name] = i;
            }

            public int Compare(object x, object y)
            {
                var a = (NumberableRowViewModel)x;
                var b = (NumberableRowViewModel)y;
                int ia = _orderMap.TryGetValue(a.RoomName, out int idxA) ? idxA : int.MaxValue;
                int ib = _orderMap.TryGetValue(b.RoomName, out int idxB) ? idxB : int.MaxValue;
                int cmp = ia.CompareTo(ib);
                if (cmp != 0) return cmp;
                string keyA = string.IsNullOrEmpty(a.Value) ? a.Mark : a.Value;
                string keyB = string.IsNullOrEmpty(b.Value) ? b.Mark : b.Value;
                return string.Compare(keyA, keyB, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
