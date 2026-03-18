#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class CircuitNumberTabViewModel : ViewModelBase
    {
        private readonly Document _doc;
        private readonly NumberCollectorService _collectorService;
        private readonly ExternalEvent _externalEvent;
        private readonly RevitApiRequestHandler _handler;

        private string _selectedPanel;
        private NumberableRowViewModel _selectedRow;
        private PanelScheduleView _currentScheduleView;
        private PanelSettingsModel _selectedPanelSettings;
        private readonly List<NumberableRowViewModel> _selectedRows = new List<NumberableRowViewModel>();
        private bool _isBusy;

        public ObservableCollection<NumberableRowViewModel> Rows { get; } = new ObservableCollection<NumberableRowViewModel>();
        public ObservableCollection<NumberableRowViewModel> AllCircuitRows { get; } = new ObservableCollection<NumberableRowViewModel>();
        public ObservableCollection<PanelSettingsModel> PanelSettings { get; } = new ObservableCollection<PanelSettingsModel>();
        public ObservableCollection<string> Panels { get; } = new ObservableCollection<string>();

        public List<string> CircuitNamingOptions => ParameterHelper.CircuitNamingOptions;

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

        public CircuitNumberTabViewModel(Document doc, List<CircuitNumberRow> circuits,
            NumberCollectorService collectorService,
            ExternalEvent externalEvent, RevitApiRequestHandler handler)
        {
            _doc = doc;
            _collectorService = collectorService;
            _externalEvent = externalEvent;
            _handler = handler;

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

            foreach (var panelName in distinctPanels)
            {
                Element panelEl = ParameterHelper.GetPanelElement(doc, panelName);
                if (panelEl == null) continue;

                string naming = ParameterHelper.GetCircuitNaming(panelEl);
                if (string.IsNullOrEmpty(naming) || !CircuitNamingOptions.Contains(naming))
                    naming = "(None)";

                PanelSettings.Add(new PanelSettingsModel(
                    panelName,
                    panelEl.Id,
                    naming,
                    ParameterHelper.GetCircuitPrefix(panelEl),
                    ParameterHelper.GetCircuitPrefixSeparator(panelEl)));
            }

            PopulateAllCircuits(circuits);

            if (Panels.Count > 0)
                SelectedPanel = Panels[0];
        }

        private void PopulateAllCircuits(List<CircuitNumberRow> circuits)
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

            Element panelEl = ParameterHelper.GetPanelElement(_doc, _selectedPanel);
            if (panelEl == null) return;

            IsBusy = true;
            RaiseRequest(new GetOrCreateScheduleViewRequest
            {
                PanelId = panelEl.Id,
                OnComplete = result =>
                {
                    _currentScheduleView = result as PanelScheduleView;
                    if (_currentScheduleView != null)
                        RequestSlotLayout();
                    else
                        IsBusy = false;
                }
            });
        }

        private void RequestSlotLayout(Action onComplete = null)
        {
            if (_currentScheduleView == null) { IsBusy = false; return; }

            RaiseRequest(new GetSlotLayoutRequest
            {
                ScheduleView = _currentScheduleView,
                OnComplete = result =>
                {
                    PopulateFromSlots(result as List<SlotInfo>);
                    if (onComplete != null)
                        onComplete();
                    else
                        IsBusy = false;
                }
            });
        }

        private void PopulateFromSlots(List<SlotInfo> slots)
        {
            Rows.Clear();
            if (slots == null) return;

            foreach (var slot in slots)
            {
                if (slot.CircuitId == null || slot.CircuitId == ElementId.InvalidElementId)
                {
                    string loadName = slot.SlotType == "Spare" ? "(Spare)"
                                    : slot.SlotType == "Space" ? "(Space)"
                                    : "";
                    var emptyRow = new NumberableRowViewModel(
                        ElementId.InvalidElementId,
                        displayLabel: $"Slot {slot.SlotNumber}",
                        value: "",
                        panel: _selectedPanel,
                        loadName: loadName);
                    emptyRow.SlotNumber = slot.SlotNumber;
                    emptyRow.SlotRow = slot.Row;
                    emptyRow.SlotCol = slot.Col;
                    emptyRow.SlotType = slot.SlotType;
                    Rows.Add(emptyRow);
                    continue;
                }

                Element el = _doc.GetElement(slot.CircuitId);
                if (el is ElectricalSystem es)
                {
                    string circuitNumber = ParameterHelper.GetCircuitNumber(es);
                    string loadName = slot.SlotType == "Spare" ? "(Spare)"
                                    : slot.SlotType == "Space" ? "(Space)"
                                    : ParameterHelper.GetLoadName(es) ?? "";
                    var row = new NumberableRowViewModel(
                        es.Id,
                        displayLabel: circuitNumber,
                        value: circuitNumber,
                        panel: _selectedPanel,
                        loadName: loadName);
                    row.SlotNumber = slot.SlotNumber;
                    row.SlotRow = slot.Row;
                    row.SlotCol = slot.Col;
                    row.SlotType = slot.SlotType;
                    Rows.Add(row);
                }
            }
        }

        private void RefreshAllCircuits()
        {
            RaiseRequest(new RefreshCircuitsRequest
            {
                OnComplete = result =>
                {
                    if (result is List<CircuitNumberRow> circuits)
                        PopulateAllCircuits(circuits);
                    IsBusy = false;
                }
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
            RaiseRequest(new MoveCircuitRequest
            {
                ScheduleView = _currentScheduleView,
                FromRow = selected.SlotRow,
                FromCol = selected.SlotCol,
                ToRow = targetRow.SlotRow,
                ToCol = targetRow.SlotCol,
                OnComplete = result =>
                {
                    if (result is true)
                    {
                        RequestSlotLayoutThenRefresh(targetSlotNumber);
                    }
                    else
                        IsBusy = false;
                }
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
            RaiseRequest(new MoveCircuitRequest
            {
                ScheduleView = _currentScheduleView,
                FromRow = selected.SlotRow,
                FromCol = selected.SlotCol,
                ToRow = targetRow.SlotRow,
                ToCol = targetRow.SlotCol,
                OnComplete = result =>
                {
                    if (result is true)
                    {
                        RequestSlotLayoutThenRefresh(targetSlotNumber);
                    }
                    else
                        IsBusy = false;
                }
            });
        }

        private void RequestSlotLayoutThenRefresh(int slotNumberToSelect = -1)
        {
            RaiseRequest(new GetSlotLayoutRequest
            {
                ScheduleView = _currentScheduleView,
                OnComplete = result =>
                {
                    PopulateFromSlots(result as List<SlotInfo>);
                    if (slotNumberToSelect >= 0)
                        SelectedRow = Rows.FirstOrDefault(r => r.SlotNumber == slotNumberToSelect);
                    RefreshAllCircuits();
                }
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
            RaiseRequest(new AssignSpareRequest
            {
                ScheduleView = _currentScheduleView,
                Slots = targets.Select(r => (r.SlotRow, r.SlotCol)).ToList(),
                OnComplete = result =>
                {
                    if (result is true)
                    {
                        RequestSlotLayoutThenRefresh(firstSlot);
                    }
                    else
                        IsBusy = false;
                }
            });
        }

        private void ExecuteAssignSpace()
        {
            if (!CanAssignSpareOrSpace()) return;
            var targets = _selectedRows.OrderBy(r => r.SlotNumber).ToList();
            int firstSlot = targets[0].SlotNumber;

            IsBusy = true;
            RaiseRequest(new AssignSpaceRequest
            {
                ScheduleView = _currentScheduleView,
                Slots = targets.Select(r => (r.SlotRow, r.SlotCol)).ToList(),
                OnComplete = result =>
                {
                    if (result is true)
                    {
                        RequestSlotLayoutThenRefresh(firstSlot);
                    }
                    else
                        IsBusy = false;
                }
            });
        }

        private void ExecuteRemoveSpareSpace()
        {
            if (!CanRemoveSpareSpace()) return;
            var targets = _selectedRows.OrderBy(r => r.SlotNumber).ToList();
            int firstSlot = targets[0].SlotNumber;

            IsBusy = true;
            RaiseRequest(new RemoveSpareSpaceRequest
            {
                ScheduleView = _currentScheduleView,
                Slots = targets.Select(r => (r.SlotRow, r.SlotCol, r.SlotType)).ToList(),
                OnComplete = result =>
                {
                    if (result is true)
                    {
                        RequestSlotLayoutThenRefresh(firstSlot);
                    }
                    else
                        IsBusy = false;
                }
            });
        }

        private bool CanOpenSchedule() => !_isBusy && _currentScheduleView != null;

        private void ExecuteOpenSchedule()
        {
            RaiseRequest(new OpenScheduleViewRequest
            {
                ScheduleView = _currentScheduleView
            });
        }

        private void Apply()
        {
            IsBusy = true;
            RaiseRequest(new WritePanelSettingsRequest
            {
                PanelSettings = PanelSettings,
                OnComplete = _ =>
                {
                    if (_currentScheduleView != null)
                    {
                        RequestSlotLayout(onComplete: () => RefreshAllCircuits());
                    }
                    else
                    {
                        RefreshAllCircuits();
                    }
                }
            });
        }

        private void RaiseRequest(RevitApiRequest request)
        {
            _handler.CurrentRequest = request;
            _externalEvent.Raise();
        }
    }
}
