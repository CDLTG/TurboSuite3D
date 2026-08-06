#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

                var (keypadCount, twoGangKeypadCount) = collectorService.GetKeypadCounts(doc);
                var (hybridRepeaterCount, hybridRepeaterPartNumber) = collectorService.GetHybridRepeaterInfo(doc);

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
                    new DmxDemandProvider(doc).GetDemand()
                };

                var viewModel = new ZonesMainViewModel(circuits,
                    keypadCount, twoGangKeypadCount, hybridRepeaterCount, hybridRepeaterPartNumber,
                    savedSettings, workQueue, loadNameWriter, panelSettingsStore, circuitSelector,
                    subsystemDemands);

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
