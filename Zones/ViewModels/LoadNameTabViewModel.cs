#nullable disable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Zones.Models;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.ViewModels
{
    public class LoadNameTabViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly ILoadNameWriter _loadNameWriter;
        private readonly ICircuitSelector _selector;

        private bool _isBusy;
        private ZonesCircuitViewModel _selectedRow;

        public LoadNameTabViewModel(List<ZonesCircuitData> circuits,
            IRevitWorkQueue workQueue, ILoadNameWriter loadNameWriter, ICircuitSelector selector)
        {
            _workQueue = workQueue;
            _loadNameWriter = loadNameWriter;
            _selector = selector;

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
            return _selectedRow.Data != null && _selectedRow.Data.CircuitId.IsValid;
        }

        private void Apply()
        {
            var circuitData = Circuits.Select(c => c.Data).ToList();

            IsBusy = true;
            _workQueue.Enqueue(
                () => _loadNameWriter.UpdateLoadNames(circuitData),
                result =>
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
                            System.Windows.MessageBox.Show($"Updated {count} electrical circuit(s).", "TurboZones");
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }

        private void SelectInProject()
        {
            if (!CanSelectInProject()) return;
            var circuitRef = _selectedRow.Data.CircuitId;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _selector.SelectInProject(circuitRef),
                result =>
                {
                    try
                    {
                        if (result is bool ok && !ok)
                            System.Windows.MessageBox.Show("This circuit no longer exists in the project.", "TurboZones");
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }
    }
}
