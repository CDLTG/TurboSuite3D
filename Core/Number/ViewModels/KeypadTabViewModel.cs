#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class RoomOrderItem : ViewModelBase
    {
        public string Name { get; }

        private int _position;
        public int Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private int _clickOrder;
        public int ClickOrder
        {
            get => _clickOrder;
            set
            {
                if (SetProperty(ref _clickOrder, value))
                    OnPropertyChanged(nameof(IsClicked));
            }
        }

        public bool IsClicked => _clickOrder > 0;

        private bool _isReordering;
        public bool IsReordering
        {
            get => _isReordering;
            set => SetProperty(ref _isReordering, value);
        }

        public RoomOrderItem(string name, int position)
        {
            Name = name;
            _position = position;
        }
    }

    public class KeypadTabViewModel : TabViewModelBase
    {
        private readonly ISwitchIdWriter _switchIdWriter;
        private readonly IRoomOrderStore _roomOrderStore;
        private bool _isSidebarVisible;
        private bool _isReordering;
        private int _nextClickOrder;
        private Dictionary<string, int> _reorderSnapshot;
        private string _searchText = string.Empty;

        public ObservableCollection<RoomOrderItem> RoomOrder { get; } = new ObservableCollection<RoomOrderItem>();

        /// <summary>
        /// Live substring filter (case-insensitive) on the Room Order list. Only surfaced
        /// in the UI during reorder mode; it filters the visible list only — click-order and
        /// Apply read the full <see cref="RoomOrder"/> collection, so a filtered-out room
        /// keeps its number and still lands in the applied order.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                    CollectionViewSource.GetDefaultView(RoomOrder).Refresh();
            }
        }

        public bool IsSidebarVisible
        {
            get => _isSidebarVisible;
            set
            {
                if (SetProperty(ref _isSidebarVisible, value))
                    OnPropertyChanged(nameof(CanUserSortColumns));
            }
        }

        public bool IsReordering
        {
            get => _isReordering;
            set
            {
                if (SetProperty(ref _isReordering, value))
                    OnPropertyChanged(nameof(IsNotReordering));
            }
        }

        public bool IsNotReordering => !_isReordering;

        public bool CanUserSortColumns => !_isSidebarVisible;

        public ICommand ToggleSidebarCommand { get; }
        public ICommand ResetRoomOrderCommand { get; }
        public ICommand StartReorderCommand { get; }
        public ICommand ApplyReorderCommand { get; }
        public ICommand CancelReorderCommand { get; }

        public KeypadTabViewModel(IReadOnlyList<NumberableRowViewModel> rows,
            IReadOnlyList<(string Name, int ClickOrder)> savedRoomOrder, bool sidebarWasOpen,
            IRevitWorkQueue workQueue, ISwitchIdWriter switchIdWriter, IRoomOrderStore roomOrderStore,
            IDeviceSelector selector)
            : base("Keypads", workQueue, selector)
        {
            _switchIdWriter = switchIdWriter;
            _roomOrderStore = roomOrderStore;
            ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
            ResetRoomOrderCommand = new RelayCommand(ResetRoomOrder);
            StartReorderCommand = new RelayCommand(StartReorder);
            ApplyReorderCommand = new RelayCommand(ApplyReorder);
            CancelReorderCommand = new RelayCommand(CancelReorder);

            foreach (var row in rows)
                AddRow(row);

            // Room order + sidebar flag are read from ExtensibleStorage at collection
            // time and passed in — a Core ctor cannot read Revit synchronously.
            for (int i = 0; i < savedRoomOrder.Count; i++)
            {
                var item = new RoomOrderItem(savedRoomOrder[i].Name, i + 1);
                item.ClickOrder = savedRoomOrder[i].ClickOrder;
                RoomOrder.Add(item);
            }

            if (sidebarWasOpen && RoomOrder.Count > 0)
            {
                MergeNewRooms();
                ApplyCustomSort();
                IsSidebarVisible = true;
            }
            else
            {
                ApplyDefaultSort();
            }

            CollectionViewSource.GetDefaultView(RoomOrder).Filter = RoomMatchesSearch;
        }

        private bool RoomMatchesSearch(object obj)
        {
            if (string.IsNullOrEmpty(_searchText)) return true;
            return obj is RoomOrderItem item &&
                   item.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ToggleSidebar()
        {
            if (!IsSidebarVisible)
            {
                if (RoomOrder.Count == 0)
                    BuildRoomOrder();
                else
                    MergeNewRooms();
                ApplyCustomSort();
            }

            IsSidebarVisible = !IsSidebarVisible;
            var isVisible = IsSidebarVisible;
            _workQueue.Enqueue(() => { _roomOrderStore.SaveSidebarVisible(isVisible); return null; }, null);
        }

        public void MoveRoom(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;
            RoomOrder.Move(fromIndex, toIndex);

            // If dragged room lands next to a clicked room, adopt a click order
            var dragged = RoomOrder[toIndex];
            if (!dragged.IsClicked)
            {
                bool neighborClicked =
                    (toIndex > 0 && RoomOrder[toIndex - 1].IsClicked) ||
                    (toIndex < RoomOrder.Count - 1 && RoomOrder[toIndex + 1].IsClicked);
                if (neighborClicked)
                    dragged.ClickOrder = 1; // placeholder, renumbered below
            }

            // Renumber all clicked rooms by list position
            int order = 1;
            foreach (var item in RoomOrder)
            {
                if (item.IsClicked)
                    item.ClickOrder = order++;
            }

            RefreshPositions();
            ApplyCustomSort();
            SaveRoomOrder();
        }

        private void ResetRoomOrder()
        {
            var result = System.Windows.MessageBox.Show(
                "Reset room order to alphabetical?",
                "TurboNumber",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.OK) return;

            BuildRoomOrder();
            ApplyCustomSort();
            SaveRoomOrder();
        }

        private void StartReorder()
        {
            SearchText = string.Empty;
            _reorderSnapshot = RoomOrder.ToDictionary(r => r.Name, r => r.ClickOrder);
            _nextClickOrder = RoomOrder.Count > 0 ? RoomOrder.Max(r => r.ClickOrder) : 0;
            foreach (var item in RoomOrder)
                item.IsReordering = true;
            IsReordering = true;
        }

        public void ToggleRoomClick(RoomOrderItem item)
        {
            if (!IsReordering) return;

            if (item.IsClicked)
            {
                int removed = item.ClickOrder;
                item.ClickOrder = 0;
                foreach (var r in RoomOrder.Where(r => r.ClickOrder > removed))
                    r.ClickOrder--;
                _nextClickOrder--;
            }
            else
            {
                _nextClickOrder++;
                item.ClickOrder = _nextClickOrder;
            }
        }

        private void ApplyReorder()
        {
            var clicked = RoomOrder
                .Where(r => r.IsClicked)
                .OrderBy(r => r.ClickOrder)
                .ToList();
            var unclicked = RoomOrder
                .Where(r => !r.IsClicked)
                .OrderBy(r => r.Name)
                .ToList();

            RoomOrder.Clear();
            int pos = 1;
            foreach (var item in clicked.Concat(unclicked))
            {
                item.IsReordering = false;
                item.Position = pos++;
                RoomOrder.Add(item);
            }

            _reorderSnapshot = null;
            IsReordering = false;
            SearchText = string.Empty;
            ApplyCustomSort();
            SaveRoomOrder();
        }

        private void CancelReorder()
        {
            SearchText = string.Empty;
            if (_reorderSnapshot != null)
            {
                foreach (var item in RoomOrder)
                {
                    item.ClickOrder = _reorderSnapshot.TryGetValue(item.Name, out int order) ? order : 0;
                    item.IsReordering = false;
                }
                _reorderSnapshot = null;
            }
            else
            {
                foreach (var item in RoomOrder)
                    item.IsReordering = false;
            }
            IsReordering = false;
        }

        private void RefreshPositions()
        {
            for (int i = 0; i < RoomOrder.Count; i++)
                RoomOrder[i].Position = i + 1;
        }

        private void BuildRoomOrder()
        {
            RoomOrder.Clear();
            var names = Rows
                .Select(r => r.RoomName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            for (int i = 0; i < names.Count; i++)
                RoomOrder.Add(new RoomOrderItem(names[i], i + 1));
        }

        private void MergeNewRooms()
        {
            var existing = new HashSet<string>(RoomOrder.Select(r => r.Name));
            var newNames = Rows
                .Select(r => r.RoomName)
                .Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            foreach (var n in newNames)
                RoomOrder.Add(new RoomOrderItem(n, RoomOrder.Count + 1));
        }

        private void ApplyCustomSort()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
            view.SortDescriptions.Clear();
            view.CustomSort = new RoomOrderComparer(RoomOrder);
        }

        private void SaveRoomOrder()
        {
            var snapshot = RoomOrder.Select(r => (r.Name, r.ClickOrder)).ToList();
            _workQueue.Enqueue(() => { _roomOrderStore.SaveRoomOrder(snapshot); return null; }, null);
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
                _orderMap = new Dictionary<string, int>(order.Count);
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
