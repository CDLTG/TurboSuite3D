#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class CircuitNumberTabViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly ICircuitNumberOperations _ops;
        private readonly IReadOnlyList<string> _circuitNamingOptions;

        private string _selectedPanel;
        private NumberableRowViewModel _selectedRow;
        // Opaque panel-schedule view handle — the VM only stores it and passes it back to
        // ops, never calling into it, so no Revit type leaks into Core.
        private object _currentScheduleView;
        private PanelSettingsModel _selectedPanelSettings;
        private readonly List<NumberableRowViewModel> _selectedRows = new List<NumberableRowViewModel>();
        private bool _isBusy;

        public ObservableCollection<NumberableRowViewModel> Rows { get; } = new ObservableCollection<NumberableRowViewModel>();
        public ObservableCollection<NumberableRowViewModel> AllCircuitRows { get; } = new ObservableCollection<NumberableRowViewModel>();
        public ObservableCollection<PanelSettingsModel> PanelSettings { get; } = new ObservableCollection<PanelSettingsModel>();
        public ObservableCollection<string> Panels { get; } = new ObservableCollection<string>();

        public IReadOnlyList<string> CircuitNamingOptions => _circuitNamingOptions;

        public string TabHeader { get; } = "Circuit Numbers";

        public ICommand ApplyCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand AssignSpareCommand { get; }
        public ICommand AssignSpaceCommand { get; }
        public ICommand RemoveSpareSpaceCommand { get; }
        public ICommand OpenScheduleCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public string SelectedPanel
        {
            get => _selectedPanel;
            set
            {
                if (SetProperty(ref _selectedPanel, value))
                    OnPanelSelected();
            }
        }

        public NumberableRowViewModel SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetProperty(ref _selectedRow, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public void SetSelectedRows(IList selectedItems)
        {
            _selectedRows.Clear();
            foreach (var item in selectedItems)
            {
                if (item is NumberableRowViewModel row)
                    _selectedRows.Add(row);
            }
            CommandManager.InvalidateRequerySuggested();
        }

        public PanelSettingsModel SelectedPanelSettings
        {
            get => _selectedPanelSettings;
            private set => SetProperty(ref _selectedPanelSettings, value);
        }

        public int AllCircuitCount => AllCircuitRows.Count;

        public CircuitNumberTabViewModel(List<CircuitNumberRow> circuits,
            List<PanelSettingsModel> panelSettings,
            IReadOnlyList<string> circuitNamingOptions,
            IRevitWorkQueue workQueue,
            ICircuitNumberOperations ops)
        {
            _workQueue = workQueue;
            _ops = ops;
            _circuitNamingOptions = circuitNamingOptions;

            ApplyCommand = new RelayCommand(Apply, () => !IsBusy);
            MoveUpCommand = new RelayCommand(ExecuteMoveUp, CanMoveUp);
            MoveDownCommand = new RelayCommand(ExecuteMoveDown, CanMoveDown);
            AssignSpareCommand = new RelayCommand(ExecuteAssignSpare, CanAssignSpareOrSpace);
            AssignSpaceCommand = new RelayCommand(ExecuteAssignSpace, CanAssignSpareOrSpace);
            RemoveSpareSpaceCommand = new RelayCommand(ExecuteRemoveSpareSpace, CanRemoveSpareSpace);
            OpenScheduleCommand = new RelayCommand(ExecuteOpenSchedule, CanOpenSchedule);

            var distinctPanels = circuits
                .Select(c => c.Panel ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            foreach (var panelName in distinctPanels)
                Panels.Add(panelName);

            // Panel settings (naming/prefix/separator + the panel's ElementRef) are read
            // from Revit shim-side at collection time and passed in.
            foreach (var ps in panelSettings)
                PanelSettings.Add(ps);

            PopulateAllCircuits(circuits);

            if (Panels.Count > 0)
                SelectedPanel = Panels[0];
        }

        private void PopulateAllCircuits(IReadOnlyList<CircuitNumberRow> circuits)
        {
            AllCircuitRows.Clear();

            foreach (var c in circuits)
            {
                string loadName = c.LoadName ?? "";
                if (loadName.Equals("SPARE", StringComparison.OrdinalIgnoreCase) ||
                    loadName.Equals("SPACE", StringComparison.OrdinalIgnoreCase))
                    continue;

                AllCircuitRows.Add(new NumberableRowViewModel(
                    c.ElementId,
                    displayLabel: c.CircuitNumber,
                    value: c.CircuitNumber,
                    panel: c.Panel ?? "",
                    loadName: loadName));
            }

            var duplicateValues = AllCircuitRows
                .Where(r => !string.IsNullOrEmpty(r.Value))
                .GroupBy(r => r.Value)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            foreach (var row in AllCircuitRows)
                row.IsDuplicate = duplicateValues.Contains(row.Value);

            OnPropertyChanged(nameof(AllCircuitCount));
        }

        private void OnPanelSelected()
        {
            Rows.Clear();
            _currentScheduleView = null;

            SelectedPanelSettings = PanelSettings.FirstOrDefault(ps => ps.PanelName == _selectedPanel);

            if (string.IsNullOrEmpty(_selectedPanel)) return;

            var panelRef = SelectedPanelSettings?.PanelElementId ?? ElementRef.None;
            if (!panelRef.IsValid) return;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.GetOrCreateScheduleView(panelRef),
                result =>
                {
                    _currentScheduleView = result;
                    if (_currentScheduleView != null)
                        RequestSlotLayout();
                    else
                        IsBusy = false;
                });
        }

        private void RequestSlotLayout(Action onComplete = null)
        {
            if (_currentScheduleView == null) { IsBusy = false; return; }

            var scheduleView = _currentScheduleView;
            _workQueue.Enqueue(
                () => _ops.GetSlotLayout(scheduleView),
                result =>
                {
                    PopulateFromSlots(result as IReadOnlyList<CircuitSlotData>);
                    if (onComplete != null)
                        onComplete();
                    else
                        IsBusy = false;
                });
        }

        private void PopulateFromSlots(IReadOnlyList<CircuitSlotData> slots)
        {
            Rows.Clear();
            if (slots == null) return;

            foreach (var slot in slots)
            {
                bool isCircuit = slot.CircuitRef.IsValid;
                var row = new NumberableRowViewModel(
                    slot.CircuitRef,
                    displayLabel: isCircuit ? slot.CircuitNumber : $"Slot {slot.SlotNumber}",
                    value: isCircuit ? slot.CircuitNumber : "",
                    panel: _selectedPanel,
                    loadName: slot.LoadName);
                row.SlotNumber = slot.SlotNumber;
                row.SlotRow = slot.SlotRow;
                row.SlotCol = slot.SlotCol;
                row.SlotType = slot.SlotType;
                Rows.Add(row);
            }
        }

        private void RefreshAllCircuits()
        {
            _workQueue.Enqueue(
                () => _ops.RefreshCircuits(),
                result =>
                {
                    if (result is IReadOnlyList<CircuitNumberRow> circuits)
                        PopulateAllCircuits(circuits);
                    IsBusy = false;
                });
        }

        private bool IsSpareOrSpace(NumberableRowViewModel row)
        {
            return row.SlotType == "Spare" || row.SlotType == "Space";
        }

        private int FindMoveTargetUp(int index)
        {
            int target = index - 1;
            while (target >= 0 && IsSpareOrSpace(Rows[target]))
                target--;
            return target;
        }

        private int FindMoveTargetDown(int index)
        {
            int target = index + 1;
            while (target < Rows.Count && IsSpareOrSpace(Rows[target]))
                target++;
            return target < Rows.Count ? target : -1;
        }

        private bool CanMoveUp()
        {
            if (_isBusy || _selectedRows.Count != 1 || _currentScheduleView == null) return false;
            var row = _selectedRows[0];
            if (row.SlotType != "Circuit") return false;
            int index = Rows.IndexOf(row);
            return FindMoveTargetUp(index) >= 0;
        }

        private bool CanMoveDown()
        {
            if (_isBusy || _selectedRows.Count != 1 || _currentScheduleView == null) return false;
            var row = _selectedRows[0];
            if (row.SlotType != "Circuit") return false;
            int index = Rows.IndexOf(row);
            return FindMoveTargetDown(index) >= 0;
        }

        private void ExecuteMoveUp()
        {
            if (!CanMoveUp()) return;
            var selected = _selectedRows[0];

            int index = Rows.IndexOf(selected);
            int targetIndex = FindMoveTargetUp(index);
            if (targetIndex < 0) return;

            var targetRow = Rows[targetIndex];
            int targetSlotNumber = targetRow.SlotNumber;

            IsBusy = true;
            var scheduleView = _currentScheduleView;
            _workQueue.Enqueue(
                () => _ops.MoveCircuit(scheduleView, selected.SlotRow, selected.SlotCol, targetRow.SlotRow, targetRow.SlotCol),
                result =>
                {
                    if (result is true)
                        RequestSlotLayoutThenRefresh(targetSlotNumber);
                    else
                        IsBusy = false;
                });
        }

        private void ExecuteMoveDown()
        {
            if (!CanMoveDown()) return;
            var selected = _selectedRows[0];

            int index = Rows.IndexOf(selected);
            int targetIndex = FindMoveTargetDown(index);
            if (targetIndex < 0) return;

            var targetRow = Rows[targetIndex];
            int targetSlotNumber = targetRow.SlotNumber;

            IsBusy = true;
            var scheduleView = _currentScheduleView;
            _workQueue.Enqueue(
                () => _ops.MoveCircuit(scheduleView, selected.SlotRow, selected.SlotCol, targetRow.SlotRow, targetRow.SlotCol),
                result =>
                {
                    if (result is true)
                        RequestSlotLayoutThenRefresh(targetSlotNumber);
                    else
                        IsBusy = false;
                });
        }

        private void RequestSlotLayoutThenRefresh(int slotNumberToSelect = -1)
        {
            var scheduleView = _currentScheduleView;
            _workQueue.Enqueue(
                () => _ops.GetSlotLayout(scheduleView),
                result =>
                {
                    PopulateFromSlots(result as IReadOnlyList<CircuitSlotData>);
                    if (slotNumberToSelect >= 0)
                        SelectedRow = Rows.FirstOrDefault(r => r.SlotNumber == slotNumberToSelect);
                    RefreshAllCircuits();
                });
        }

        private bool CanAssignSpareOrSpace()
        {
            return !_isBusy && _currentScheduleView != null
                && _selectedRows.Count > 0
                && _selectedRows.All(r => r.SlotType == "Empty");
        }

        private bool CanRemoveSpareSpace()
        {
            return !_isBusy && _currentScheduleView != null
                && _selectedRows.Count > 0
                && _selectedRows.All(r => r.SlotType == "Spare" || r.SlotType == "Space");
        }

        private void ExecuteAssignSpare()
        {
            if (!CanAssignSpareOrSpace()) return;
            var targets = _selectedRows.OrderBy(r => r.SlotNumber).ToList();
            int firstSlot = targets[0].SlotNumber;

            IsBusy = true;
            var scheduleView = _currentScheduleView;
            var slots = targets.Select(r => (r.SlotRow, r.SlotCol)).ToList();
            _workQueue.Enqueue(
                () => _ops.AssignSpare(scheduleView, slots),
                result =>
                {
                    if (result is true)
                        RequestSlotLayoutThenRefresh(firstSlot);
                    else
                        IsBusy = false;
                });
        }

        private void ExecuteAssignSpace()
        {
            if (!CanAssignSpareOrSpace()) return;
            var targets = _selectedRows.OrderBy(r => r.SlotNumber).ToList();
            int firstSlot = targets[0].SlotNumber;

            IsBusy = true;
            var scheduleView = _currentScheduleView;
            var slots = targets.Select(r => (r.SlotRow, r.SlotCol)).ToList();
            _workQueue.Enqueue(
                () => _ops.AssignSpace(scheduleView, slots),
                result =>
                {
                    if (result is true)
                        RequestSlotLayoutThenRefresh(firstSlot);
                    else
                        IsBusy = false;
                });
        }

        private void ExecuteRemoveSpareSpace()
        {
            if (!CanRemoveSpareSpace()) return;
            var targets = _selectedRows.OrderBy(r => r.SlotNumber).ToList();
            int firstSlot = targets[0].SlotNumber;

            IsBusy = true;
            var scheduleView = _currentScheduleView;
            var slots = targets.Select(r => (r.SlotRow, r.SlotCol, r.SlotType)).ToList();
            _workQueue.Enqueue(
                () => _ops.RemoveSpareSpace(scheduleView, slots),
                result =>
                {
                    if (result is true)
                        RequestSlotLayoutThenRefresh(firstSlot);
                    else
                        IsBusy = false;
                });
        }

        private bool CanOpenSchedule() => !_isBusy && _currentScheduleView != null;

        private void ExecuteOpenSchedule()
        {
            var scheduleView = _currentScheduleView;
            _workQueue.Enqueue(() => { _ops.OpenScheduleView(scheduleView); return null; }, null);
        }

        private void Apply()
        {
            IsBusy = true;
            _workQueue.Enqueue(
                () => { _ops.WritePanelSettings(PanelSettings); return null; },
                _ =>
                {
                    if (_currentScheduleView != null)
                        RequestSlotLayout(onComplete: () => RefreshAllCircuits());
                    else
                        RefreshAllCircuits();
                });
        }
    }
}
