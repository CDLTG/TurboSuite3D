#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.ViewModels
{
    public class ZonesMainViewModel : ViewModelBase
    {
        public ZonesMainViewModel(Document doc, List<ZonesCircuitData> circuits,
            int keypadCount, int twoGangKeypadCount,
            int hybridRepeaterCount, string hybridRepeaterPartNumber,
            ExternalEvent externalEvent, RevitApiRequestHandler handler)
        {
            PanelTab = new PanelBreakdownTabViewModel(doc, circuits,
                keypadCount, twoGangKeypadCount, hybridRepeaterCount, hybridRepeaterPartNumber,
                externalEvent, handler);
            LoadNameTab = new LoadNameTabViewModel(doc, circuits, externalEvent, handler);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }
    }
}
