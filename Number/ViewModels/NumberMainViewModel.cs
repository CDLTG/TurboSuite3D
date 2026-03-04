#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;

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
            NumberCollectorService collectorService)
        {
            var writerService = new NumberWriterService();

            CircuitTab = new CircuitNumberTabViewModel(doc, circuits, writerService, collectorService);
            KeypadTab = new KeypadTabViewModel(doc, keypads, writerService);
            PowerSupplyTab = new PowerSupplyTabViewModel(doc, powerSupplies, writerService);
        }
    }
}
