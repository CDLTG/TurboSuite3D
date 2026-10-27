#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Services;
using TurboSuite.Number.ViewModels;
using TurboSuite.Number.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number
{
    /// <summary>
    /// TurboNumber — modeless window for managing circuit numbers, keypad Switch IDs, and
    /// power-supply Switch IDs. All Revit writes go through <see cref="IExternalEventHandler"/>;
    /// see CLAUDE.md "Modeless pattern" for the request-queue + completion-callback model.
    ///
    /// <para><b>Worksharing:</b> the window holds a session lease (<see cref="WorksharingLease.AcquireSession"/>)
    /// on the TurboNumber lock store plus its auto-saved stores, taken before the window opens; each write to
    /// circuits, panels, schedule views or devices borrows just those per operation. The borrows hold until the
    /// user syncs, and a successful sync closes the window (<see cref="ModelessWindowGuard"/>).</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class NumberCommand : IExternalCommand
    {
        private static TurboNumberWindow _activeWindow;

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
                    TaskDialog.Show("TurboNumber", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboNumber", "Please close any active transactions before opening TurboNumber.");
                    return Result.Failed;
                }

                var collectorService = new NumberCollectorService();
                var circuits = collectorService.GetCircuits(doc);
                var keypads = collectorService.GetKeypads(doc);
                var powerSupplies = collectorService.GetPowerSupplies(doc);
                var allRoomNames = collectorService.GetAllRoomNames(doc);

                if (circuits.Count == 0 && keypads.Count == 0 && powerSupplies.Count == 0)
                {
                    TaskDialog.Show("TurboNumber",
                        "No circuits or numberable devices found.\n\n" +
                        "Please ensure:\n" +
                        "• Electrical circuits are assigned to panels\n" +
                        "• Keypad or Power Supply lighting devices exist in the project");
                    return Result.Cancelled;
                }

                // Returns Succeeded on denial, not Cancelled: a first-ever open may have just committed the
                // lock store, and a non-Succeeded return would roll it back.
                var lease = WorksharingLease.AcquireSession(doc, "TurboNumber", SessionWorkingSet(doc), "setting");
                if (!lease.IsGranted)
                {
                    TaskDialog.Show("TurboNumber", lease.DenialMessage);
                    return Result.Succeeded;
                }

                var writerService = new NumberWriterService();
                var panelScheduleService = new PanelScheduleService();

                // Work-queue + Revit-free operation impls — all three tabs are Core VMs now.
                var workQueue = new RevitWorkQueue("TurboNumber Error", "TurboNumber Work Queue");
                var switchIdWriter = new SwitchIdWriter(doc, writerService);
                var prefixSuffixStore = new PrefixSuffixStore(doc);
                var roomOrderStore = new RoomOrderStore(doc);
                var circuitOps = new CircuitNumberOperations(doc, uidoc,
                    panelScheduleService, writerService, collectorService);
                var deviceSelector = new DeviceSelector(uidoc);

                var viewModel = new NumberMainViewModel(doc, circuits, keypads, powerSupplies,
                    allRoomNames, workQueue, switchIdWriter, prefixSuffixStore, roomOrderStore,
                    circuitOps, deviceSelector);

                var window = new TurboNumberWindow
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
                TaskDialog.Show("TurboNumber Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>The auto-saved stores the window writes, where they already exist. Circuits, panels and
        /// devices are leased per operation (<see cref="NumberWorksharing"/>) so teammates aren't blocked for
        /// the whole session.</summary>
        private static List<ElementId> SessionWorkingSet(Document doc)
        {
            var ids = new List<ElementId>(CircuitNamingStorageService.FindStorageIds(doc));
            var roomOrder = RoomOrderStorageService.FindStorageId(doc);
            if (roomOrder != null) ids.Add(roomOrder);
            var roomLocation = RoomLocationStorageService.FindStorageId(doc);
            if (roomLocation != null) ids.Add(roomLocation);
            return ids;
        }
    }
}
