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
            KeypadCounts keypadCounts,
            ControlDeviceGroup hybridRepeaters,
            PanelSettings savedSettings,
            IRevitWorkQueue workQueue,
            ILoadNameWriter loadNameWriter,
            IPanelSettingsStore panelSettingsStore,
            ICircuitSelector circuitSelector,
            IReadOnlyList<ControlSubsystemDemand> subsystemDemands = null)
        {
            PanelTab = new PanelBreakdownTabViewModel(circuits,
                keypadCounts, hybridRepeaters,
                savedSettings, workQueue, panelSettingsStore, subsystemDemands);
            LoadNameTab = new LoadNameTabViewModel(circuits, workQueue, loadNameWriter, circuitSelector);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }
    }
}
