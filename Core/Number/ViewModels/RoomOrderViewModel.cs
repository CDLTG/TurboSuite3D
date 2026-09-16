#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    /// <summary>
    /// Window-level, tab-independent editor for the project-wide room order: the ordered
    /// room list plus its click-order + drag reorder gestures. Hoisted out of
    /// <see cref="KeypadTabViewModel"/> so the permanent sidebar and every consumer
    /// (keypad numbering, the CircuitNumber "Sort by room order") share one order.
    ///
    /// Population is the project-wide room enumeration (all Spaces/regions), injected as
    /// <c>allRoomNames</c>, not just keypad rows — so keypad-less and circuit-less rooms
    /// are orderable and the click-order gesture works for every room. Raises
    /// <see cref="OrderChanged"/> whenever the committed order changes (apply / move /
    /// reset), which the keypad grid subscribes to to re-run its custom sort.
    /// </summary>
    public class RoomOrderViewModel : ViewModelBase
    {
        private readonly IRoomOrderStore _roomOrderStore;
        private readonly IRevitWorkQueue _workQueue;
        private readonly IReadOnlyList<string> _allRoomNames;
        private bool _isReordering;
        private int _nextClickOrder;
        private Dictionary<string, int> _reorderSnapshot;
        private string _searchText = string.Empty;

        public ObservableCollection<RoomOrderItem> RoomOrder { get; } = new ObservableCollection<RoomOrderItem>();

        /// <summary>Raised after the committed room order changes (apply/move/reset), so
        /// consumers such as the keypad grid can re-sort against the new order.</summary>
        public event Action OrderChanged;

        /// <summary>Current display order of room names (top-to-bottom). The keys are the
        /// same strings circuit-room resolution produces, so a downstream sort keyed on
        /// this list never drifts from the list itself.</summary>
        public IReadOnlyList<string> OrderedRoomNames => RoomOrder.Select(r => r.Name).ToList();

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

        public ICommand ResetRoomOrderCommand { get; }
        public ICommand StartReorderCommand { get; }
        public ICommand ApplyReorderCommand { get; }
        public ICommand CancelReorderCommand { get; }

        public RoomOrderViewModel(IReadOnlyList<string> allRoomNames,
            IReadOnlyList<(string Name, int ClickOrder)> savedRoomOrder,
            IRevitWorkQueue workQueue, IRoomOrderStore roomOrderStore)
        {
            _allRoomNames = allRoomNames ?? Array.Empty<string>();
            _workQueue = workQueue;
            _roomOrderStore = roomOrderStore;

            ResetRoomOrderCommand = new RelayCommand(ResetRoomOrder);
            StartReorderCommand = new RelayCommand(StartReorder);
            ApplyReorderCommand = new RelayCommand(ApplyReorder);
            CancelReorderCommand = new RelayCommand(CancelReorder);

            // Room order is read from ExtensibleStorage at collection time and passed in —
            // a Core ctor cannot read Revit synchronously. Seed from the persisted order,
            // then fold in any rooms that have appeared since (append-alphabetical).
            for (int i = 0; i < savedRoomOrder.Count; i++)
            {
                var item = new RoomOrderItem(savedRoomOrder[i].Name, i + 1);
                item.ClickOrder = savedRoomOrder[i].ClickOrder;
                RoomOrder.Add(item);
            }

            if (RoomOrder.Count == 0)
                BuildRoomOrder();
            else
                MergeNewRooms();
            RefreshPositions();

            CollectionViewSource.GetDefaultView(RoomOrder).Filter = RoomMatchesSearch;
        }

        private bool RoomMatchesSearch(object obj)
        {
            if (string.IsNullOrEmpty(_searchText)) return true;
            return obj is RoomOrderItem item &&
                   item.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
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
            SaveRoomOrder();
            OrderChanged?.Invoke();
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
            SaveRoomOrder();
            OrderChanged?.Invoke();
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
            SaveRoomOrder();
            OrderChanged?.Invoke();
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
            var names = _allRoomNames
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
            for (int i = 0; i < names.Count; i++)
                RoomOrder.Add(new RoomOrderItem(names[i], i + 1));
        }

        private void MergeNewRooms()
        {
            var existing = new HashSet<string>(RoomOrder.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var newNames = _allRoomNames
                .Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
            foreach (var n in newNames)
                RoomOrder.Add(new RoomOrderItem(n, RoomOrder.Count + 1));
        }

        private void SaveRoomOrder()
        {
            var snapshot = RoomOrder.Select(r => (r.Name, r.ClickOrder)).ToList();
            _workQueue.Enqueue(() => { _roomOrderStore.SaveRoomOrder(snapshot); return null; }, null);
        }
    }
}
