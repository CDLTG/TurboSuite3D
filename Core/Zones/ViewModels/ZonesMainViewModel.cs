#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
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
            IReadOnlyList<ControlSubsystemDemand> subsystemDemands = null,
            IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> daliModulesByZone = null,
            IReadOnlyList<DaliZoneItemViewModel> daliZones = null,
            IReadOnlyList<int> daliPanelZones = null,
            DaliModuleState savedDaliState = null,
            IDaliLoopStore daliLoopStore = null)
        {
            PanelTab = new PanelBreakdownTabViewModel(circuits,
                keypadCounts, hybridRepeaters,
                savedSettings, workQueue, panelSettingsStore, subsystemDemands, daliModulesByZone);
            LoadNameTab = new LoadNameTabViewModel(circuits, workQueue, loadNameWriter, circuitSelector);

            // The DALI tab is optional: only wired when the shim supplied a store (it always does in Revit;
            // omitted keeps existing Core tests that build the VM directly unaffected).
            if (daliLoopStore != null)
                DaliTab = new DaliTabViewModel(
                    daliZones ?? new List<DaliZoneItemViewModel>(),
                    daliPanelZones ?? new List<int>(),
                    savedDaliState ?? new DaliModuleState(),
                    workQueue, daliLoopStore);
        }

        public PanelBreakdownTabViewModel PanelTab { get; }
        public LoadNameTabViewModel LoadNameTab { get; }

        /// <summary>The DALI loop-declaration tab (Phase 3e). Null when no store was supplied — the XAML
        /// tab hides itself in that case.</summary>
        public DaliTabViewModel DaliTab { get; }

        public bool HasDaliTab => DaliTab != null;
    }
}
