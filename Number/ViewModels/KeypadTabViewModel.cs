#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
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
        private readonly Document _doc;
        private readonly ExternalEvent _externalEvent;
        private readonly RevitApiRequestHandler _handler;
        private bool _isSidebarVisible;
        private bool _isReordering;
        private int _nextClickOrder;
        private Dictionary<string, int> _reorderSnapshot;

        public ObservableCollection<RoomOrderItem> RoomOrder { get; } = new ObservableCollection<RoomOrderItem>();

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

        public KeypadTabViewModel(Document doc, List<DeviceNumberRow> keypads,
            ExternalEvent externalEvent, RevitApiRequestHandler handler)
            : base("Keypads", externalEvent, handler)
        {
            _doc = doc;
            _externalEvent = externalEvent;
            _handler = handler;
            ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
            ResetRoomOrderCommand = new RelayCommand(ResetRoomOrder);
            StartReorderCommand = new RelayCommand(StartReorder);
            ApplyReorderCommand = new RelayCommand(ApplyReorder);
            CancelReorderCommand = new RelayCommand(CancelReorder);

            foreach (var d in keypads)
            {
                AddRow(new NumberableRowViewModel(
                    d.ElementId,
                    d.Model,
                    d.SwitchId,
                    d.RoomName,
                    d.RoomNumber,
                    typeName: d.TypeName,
                    mark: d.Mark));
            }

            var savedOrder = RoomOrderStorageService.Load(doc);
            for (int i = 0; i < savedOrder.Count; i++)
                RoomOrder.Add(new RoomOrderItem(savedOrder[i], i + 1));

            var sidebarWasOpen = RoomOrderStorageService.LoadSidebarVisible(doc);
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
            RaiseRequest(new SaveSidebarVisibleRequest { IsVisible = IsSidebarVisible });
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
            ApplyCustomSort();
            SaveRoomOrder();
        }

        private void CancelReorder()
        {
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
            RaiseRequest(new SaveRoomOrderRequest
            {
                RoomOrder = RoomOrder.Select(r => r.Name).ToList()
            });
        }

        protected override void Apply()
        {
            RaiseRequest(new WriteDeviceSwitchIdsRequest { Rows = Rows });
        }

        private void RaiseRequest(RevitApiRequest request)
        {
            _handler.CurrentRequest = request;
            _externalEvent.Raise();
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
