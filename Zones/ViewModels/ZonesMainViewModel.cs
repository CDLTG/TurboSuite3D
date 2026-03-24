#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.ViewModels
{
    public class ZonesMainViewModel : ViewModelBase
    {
        public ZonesMainViewModel(Document doc, List<ZonesCircuitData> circuits,
            int keypadCount = 0, int twoGangKeypadCount = 0,
            int hybridRepeaterCount = 0, string hybridRepeaterPartNumber = null)
        {
            PanelTab = new PanelBreakdownTabViewModel(doc, circuits,
                keypadCount, twoGangKeypadCount, hybridRepeaterCount, hybridRepeaterPartNumber);
            LoadNameTab = new LoadNameTabViewModel(doc, circuits);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }
    }
}
