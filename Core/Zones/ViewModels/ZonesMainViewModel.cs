#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.ViewModels
{
    public class ZonesMainViewModel : ViewModelBase
    {
        public ZonesMainViewModel(List<ZonesCircuitData> circuits,
            int keypadCount, int twoGangKeypadCount,
            int hybridRepeaterCount, string hybridRepeaterPartNumber,
            PanelSettings savedSettings,
            IRevitWorkQueue workQueue,
            ILoadNameWriter loadNameWriter,
            IPanelSettingsStore panelSettingsStore,
            ICircuitSelector circuitSelector)
        {
            PanelTab = new PanelBreakdownTabViewModel(circuits,
                keypadCount, twoGangKeypadCount, hybridRepeaterCount, hybridRepeaterPartNumber,
                savedSettings, workQueue, panelSettingsStore);
            LoadNameTab = new LoadNameTabViewModel(circuits, workQueue, loadNameWriter, circuitSelector);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }
    }
}
