#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using TurboSuite.Zones.ViewModels;
using TurboSuite.Zones.Views;

namespace TurboSuite.Zones
{
    /// <summary>
    /// TurboZones — modeless window for managing circuit load names and visualizing dimmer-panel
    /// load distribution. All Revit writes go through <see cref="IExternalEventHandler"/>; see
    /// CLAUDE.md "Modeless pattern".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ZonesCommand : IExternalCommand
    {
        private static TurboZonesWindow _activeWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (_activeWindow != null)
                {
                    _activeWindow.Activate();
                    return Result.Succeeded;
                }

                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("TurboZones", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboZones", "Please close any active transactions before opening TurboZones.");
                    return Result.Failed;
                }

                var collectorService = new ZonesCollectorService();
                var circuits = collectorService.GetCircuits(doc);

                if (circuits.Count == 0)
                {
                    TaskDialog.Show("TurboZones",
                        "No electrical circuits with lighting fixtures found.\n\n" +
                        "Please ensure electrical circuits are assigned to lighting fixtures.");
                    return Result.Cancelled;
                }

                var keypadCounts = collectorService.GetKeypadCounts(doc);
                var hybridRepeaters = collectorService.GetHybridRepeaters(doc);

                // Load persisted panel settings shim-side (a Core ctor cannot read Revit synchronously).
                var savedSettings = ZonesPanelSettingsStorageService.Load(doc);

                // Work-queue + Revit-free operation impls — both tabs are Core VMs now.
                var workQueue = new RevitWorkQueue("TurboZones Error", "TurboZones Work Queue");
                var loadNameWriter = new LoadNameWriter(doc, new LoadNameService());
                var panelSettingsStore = new PanelSettingsStore(doc);
                var circuitSelector = new CircuitSelector(uidoc);

                // What the control subsystems report they need. Read once, here, for the same reason
                // the keypad counts are: the Core VM cannot touch Revit synchronously. TurboDMX solves
                // its own design, so the interface count on the BOM is channel math, not a dropdown.
                var subsystemDemands = new List<ControlSubsystemDemand>
                {
                    new DmxDemandProvider(doc).GetDemand(),
                    new ShadeDemandProvider(doc).GetDemand(),
                    new DaliDemandProvider(doc).GetDemand()
                };

                // DALI *placement* half — read once at open, same rule as the demands and keypad counts. Loop
                // DECLARATION moved to the standalone TurboDALI command; TurboZones is now a pure consumer of
                // the persisted DALI state. The order/link budget rides subsystemDemands above; this is the
                // zone→module map the persisted loop assignments already imply for the panel breakdown.
                var daliLoadsByZone = DaliDemandProvider.CountDaliLoadsByZone(doc, out _);
                var savedDaliState = DaliStorageService.Load(doc);
                var daliModulesByZone = DaliPlacementMapper.Build(savedDaliState.Loops, daliLoadsByZone).ByZone;

                var viewModel = new ZonesMainViewModel(circuits,
                    keypadCounts, hybridRepeaters,
                    savedSettings, workQueue, loadNameWriter, panelSettingsStore, circuitSelector,
                    subsystemDemands, daliModulesByZone);

                var window = new TurboZonesWindow
                {
                    DataContext = viewModel
                };

                var revitHandle = commandData.Application.MainWindowHandle;
                new WindowInteropHelper(window) { Owner = revitHandle };

                window.Closed += (s, e) =>
                {
                    _activeWindow = null;
                    workQueue.Dispose();
                };

                ModelessWindowGuard.Register(doc, window, window.Close);
                _activeWindow = window;
                window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboZones Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
