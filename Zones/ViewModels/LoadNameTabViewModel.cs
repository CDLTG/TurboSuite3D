#nullable disable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Zones.Models;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.ViewModels
{
    public class LoadNameTabViewModel : ViewModelBase
    {
        private readonly Document _doc;
        private readonly ExternalEvent _externalEvent;
        private readonly RevitApiRequestHandler _handler;

        private bool _isBusy;
        private ZonesCircuitViewModel _selectedRow;

        public LoadNameTabViewModel(Document doc, List<ZonesCircuitData> circuits,
            ExternalEvent externalEvent, RevitApiRequestHandler handler)
        {
            _doc = doc;
            _externalEvent = externalEvent;
            _handler = handler;

            Circuits = new ObservableCollection<ZonesCircuitViewModel>(
                circuits
                    .OrderBy(c => c.CircuitNumber)
                    .Select(c => new ZonesCircuitViewModel(c)));

            ApplyCommand = new RelayCommand(Apply, () => !_isBusy);
            SelectInProjectCommand = new RelayCommand(SelectInProject, CanSelectInProject);
        }

        public string TabHeader => "Load Names";

        public ObservableCollection<ZonesCircuitViewModel> Circuits { get; }

        public ICommand ApplyCommand { get; }
        public ICommand SelectInProjectCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public ZonesCircuitViewModel SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (_selectedRow == value) return;
                if (_selectedRow != null) _selectedRow.IsActiveRow = false;
                if (SetProperty(ref _selectedRow, value))
                {
                    if (_selectedRow != null) _selectedRow.IsActiveRow = true;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private bool CanSelectInProject()
        {
            if (_isBusy || _selectedRow == null) return false;
            var id = _selectedRow.Data?.CircuitId;
            return id != null && id != ElementId.InvalidElementId;
        }

        private void Apply()
        {
            var circuitData = Circuits.Select(c => c.Data).ToList();

            IsBusy = true;
            _handler.CurrentRequest = new UpdateLoadNamesRequest
            {
                Circuits = circuitData,
                OnComplete = result =>
                {
                    try
                    {
                        if (result is int count)
                        {
                            foreach (var vm in Circuits)
                            {
                                if (!string.IsNullOrWhiteSpace(vm.Data.UpdatedLoadName))
                                    vm.CurrentLoadName = vm.Data.UpdatedLoadName;
                            }
                            TaskDialog.Show("TurboZones", $"Updated {count} electrical circuit(s).");
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                }
            };
            _externalEvent.Raise();
        }

        private void SelectInProject()
        {
            if (!CanSelectInProject()) return;
            var id = _selectedRow.Data.CircuitId;

            IsBusy = true;
            _handler.CurrentRequest = new SelectInProjectRequest
            {
                CircuitId = id,
                OnComplete = result =>
                {
                    try
                    {
                        if (result is bool ok && !ok)
                            TaskDialog.Show("TurboZones", "This circuit no longer exists in the project.");
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                }
            };
            _externalEvent.Raise();
        }
    }
}
