#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
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
            RevitApiRequestHandler handler)
        {
            CircuitTab = new CircuitNumberTabViewModel(doc, circuits, collectorService, externalEvent, handler);
            KeypadTab = new KeypadTabViewModel(doc, keypads, externalEvent, handler);
            PowerSupplyTab = new PowerSupplyTabViewModel(doc, powerSupplies, externalEvent, handler);
        }
    }
}
