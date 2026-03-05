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

        public LoadNameTabViewModel(Document doc, List<ZonesCircuitData> circuits)
        {
            _doc = doc;
            Circuits = new ObservableCollection<ZonesCircuitViewModel>(
                circuits
                    .OrderBy(c => c.CircuitNumber)
                    .Select(c => new ZonesCircuitViewModel(c)));

            ApplyCommand = new RelayCommand(Apply);
        }

        public string TabHeader => "Load Names";

        public ObservableCollection<ZonesCircuitViewModel> Circuits { get; }

        public ICommand ApplyCommand { get; }

        private void Apply()
        {
            var circuitData = Circuits.Select(c => c.Data).ToList();
            var service = new LoadNameService();
            int count = service.UpdateLoadNames(_doc, circuitData);

            // Refresh Current Load Name column to reflect what was just written
            foreach (var vm in Circuits)
            {
                if (!string.IsNullOrWhiteSpace(vm.Data.UpdatedLoadName))
                    vm.CurrentLoadName = vm.Data.UpdatedLoadName;
            }

            TaskDialog.Show("TurboZones", $"Updated {count} electrical circuit(s).");
        }
    }
}
