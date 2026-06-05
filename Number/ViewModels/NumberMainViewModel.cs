#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class NumberMainViewModel : ViewModelBase
    {
        public CircuitNumberTabViewModel CircuitTab { get; }
        public KeypadTabViewModel KeypadTab { get; }
        public PowerSupplyTabViewModel PowerSupplyTab { get; }

        public NumberMainViewModel(Document doc,
            List<CircuitNumberRow> circuits,
            List<DeviceNumberRow> keypads,
            List<DeviceNumberRow> powerSupplies,
            NumberCollectorService collectorService,
            ExternalEvent externalEvent,
            RevitApiRequestHandler handler,
            IRevitWorkQueue workQueue,
            ISwitchIdWriter switchIdWriter,
            IPrefixSuffixStore prefixSuffixStore)
        {
            CircuitTab = new CircuitNumberTabViewModel(doc, circuits, collectorService, externalEvent, handler);
            KeypadTab = new KeypadTabViewModel(doc, keypads, externalEvent, handler);

            // Project the Revit-coupled DeviceNumberRow into the Revit-free Core row VM
            // shim-side (the .ToRef() conversions live in the shim), then hand the Core
            // PowerSupply tab only abstractions.
            var psRows = powerSupplies.Select(d => new NumberableRowViewModel(
                d.ElementId.ToRef(),
                d.Model,
                d.SwitchId,
                circuitNumber: d.CircuitNumber,
                circuitElementId: d.CircuitElementId.ToRef(),
                loadName: d.LoadName,
                typeName: d.TypeName,
                mark: d.Mark)).ToList();

            var (savedPrefix, savedSuffix) = RoomOrderStorageService.LoadPrefixSuffix(doc);

            PowerSupplyTab = new PowerSupplyTabViewModel(psRows, savedPrefix, savedSuffix,
                workQueue, switchIdWriter, prefixSuffixStore);
        }
    }
}
