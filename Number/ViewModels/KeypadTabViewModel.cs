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
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class KeypadTabViewModel : TabViewModelBase
    {
        private readonly Document _doc;
        private readonly NumberWriterService _writerService;
        private bool _isSidebarVisible;

        public ObservableCollection<string> RoomOrder { get; } = new ObservableCollection<string>();

        public bool IsSidebarVisible
        {
            get => _isSidebarVisible;
            set
            {
                if (SetProperty(ref _isSidebarVisible, value))
                    OnPropertyChanged(nameof(CanUserSortColumns));
            }
        }

        public bool CanUserSortColumns => !_isSidebarVisible;

        public ICommand ToggleSidebarCommand { get; }
        public ICommand ResetRoomOrderCommand { get; }

        public KeypadTabViewModel(Document doc, List<DeviceNumberRow> keypads, NumberWriterService writerService)
            : base("Keypads")
        {
            _doc = doc;
            _writerService = writerService;
            ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
            ResetRoomOrderCommand = new RelayCommand(ResetRoomOrder);

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
            foreach (var name in savedOrder)
                RoomOrder.Add(name);

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
            RoomOrderStorageService.SaveSidebarVisible(_doc, IsSidebarVisible);
        }

        public void MoveRoom(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;
            RoomOrder.Move(fromIndex, toIndex);
            ApplyCustomSort();
            RoomOrderStorageService.Save(_doc, RoomOrder.ToList());
        }

        private void ResetRoomOrder()
        {
            BuildRoomOrder();
            ApplyCustomSort();
            RoomOrderStorageService.Save(_doc, RoomOrder.ToList());
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
            foreach (var n in names)
                RoomOrder.Add(n);
        }

        private void MergeNewRooms()
        {
            var existing = new HashSet<string>(RoomOrder);
            var newNames = Rows
                .Select(r => r.RoomName)
                .Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            foreach (var n in newNames)
                RoomOrder.Add(n);
        }

        private void ApplyCustomSort()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
            view.SortDescriptions.Clear();
            view.CustomSort = new RoomOrderComparer(RoomOrder);
        }

        protected override void Apply()
        {
            _writerService.WriteDeviceSwitchIds(_doc, Rows);
        }

        private class RoomOrderComparer : IComparer
        {
            private readonly Dictionary<string, int> _orderMap;

            public RoomOrderComparer(ObservableCollection<string> order)
            {
                _orderMap = new Dictionary<string, int>(order.Count);
                for (int i = 0; i < order.Count; i++)
                    _orderMap[order[i]] = i;
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
