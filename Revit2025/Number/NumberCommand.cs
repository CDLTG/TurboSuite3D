#nullable disable
using System;
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

                if (circuits.Count == 0 && keypads.Count == 0 && powerSupplies.Count == 0)
                {
                    TaskDialog.Show("TurboNumber",
                        "No circuits or numberable devices found.\n\n" +
                        "Please ensure:\n" +
                        "• Electrical circuits are assigned to panels\n" +
                        "• Keypad or Power Supply lighting devices exist in the project");
                    return Result.Cancelled;
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

                var viewModel = new NumberMainViewModel(doc, circuits, keypads, powerSupplies,
                    workQueue, switchIdWriter, prefixSuffixStore, roomOrderStore, circuitOps);

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
    }
}
