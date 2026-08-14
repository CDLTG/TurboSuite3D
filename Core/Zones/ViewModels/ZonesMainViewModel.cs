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
        // DALI loop DECLARATION moved out to the standalone TurboDALI command; TurboZones stays a pure
        // CONSUMER of the persisted DALI state — the shim reads it and hands the placement map
        // (daliModulesByZone) to the Panel Breakdown, and DaliDemandProvider carries the BOM/link demand.
        public ZonesMainViewModel(List<ZonesCircuitData> circuits,
            KeypadCounts keypadCounts,
            ControlDeviceGroup hybridRepeaters,
            PanelSettings savedSettings,
            IRevitWorkQueue workQueue,
            ILoadNameWriter loadNameWriter,
            IPanelSettingsStore panelSettingsStore,
            ICircuitSelector circuitSelector,
            IReadOnlyList<ControlSubsystemDemand> subsystemDemands = null,
            IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> daliModulesByZone = null,
            IReadOnlyList<ShadeLocationTally> shadeLocations = null)
        {
            PanelTab = new PanelBreakdownTabViewModel(circuits,
                keypadCounts, hybridRepeaters,
                savedSettings, workQueue, panelSettingsStore, subsystemDemands, daliModulesByZone,
                shadeLocations);
            LoadNameTab = new LoadNameTabViewModel(circuits, workQueue, loadNameWriter, circuitSelector);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }
    }
}
