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
            IReadOnlyList<ShadeLocationTally> shadeLocations = null,
            List<ZonesCircuitData> shadeCircuits = null,
            ILoadNameWriter shadeLoadNameWriter = null)
        {
            PanelTab = new PanelBreakdownTabViewModel(circuits,
                keypadCounts, hybridRepeaters,
                savedSettings, workQueue, panelSettingsStore, subsystemDemands, daliModulesByZone,
                shadeLocations);
            LoadNameTab = new LoadNameTabViewModel(circuits, workQueue, loadNameWriter, circuitSelector);

            // Shade Names — the same Load-Names grid fed shade circuits and the shade override store.
            // Only appears on jobs that actually have shade circuits, so non-shade jobs keep the
            // two-tab layout untouched.
            if (shadeCircuits != null && shadeCircuits.Count > 0 && shadeLoadNameWriter != null)
                ShadeNameTab = new LoadNameTabViewModel(
                    shadeCircuits, workQueue, shadeLoadNameWriter, circuitSelector, "Shade Names");
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }

        /// <summary>The Shade Names tab, or null when no shade writer was supplied. The view binds a
        /// tab whose visibility follows this being non-null.</summary>
        public LoadNameTabViewModel ShadeNameTab { get; }

        public bool HasShadeNameTab => ShadeNameTab != null;
    }
}
