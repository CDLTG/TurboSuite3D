#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Driver.Services;
using TurboSuite.Driver.ViewModels;
using TurboSuite.Driver.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Driver
{
    /// <summary>
    /// TurboRPS — modeless staleness dashboard + batch in-place driver-type corrector.
    /// Re-runs the driver selection across all RPS circuits, flags stale ones, and fixes
    /// Case-A drift (same family + same driver count) via an in-place
    /// <c>FamilyInstance.Symbol</c> swap. Mirrors the TurboZones modeless pattern; all Revit
    /// writes go through <see cref="IExternalEventHandler"/> (see CLAUDE.md "Modeless pattern").
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RPSCommand : IExternalCommand
    {
        private static TurboRPSWindow _activeWindow;

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
                    TaskDialog.Show("TurboRPS", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboRPS", "Please close any active transactions before opening TurboRPS.");
                    return Result.Failed;
                }

                var circuits = RpsCircuitDataBuilder.Build(doc);
                var matchCatalog = DriverMatchOperations.Build(doc);

                // Bail only when BOTH tabs would be empty — no RPS circuits AND no driver/fixture types
                // to compare. With no circuits but types present (e.g. mid family-build), the window still
                // opens so the Driver Match tab is usable.
                if (circuits.Count == 0 && matchCatalog.Drivers.Count == 0 && matchCatalog.Fixtures.Count == 0)
                {
                    TaskDialog.Show("TurboRPS",
                        "Nothing to show.\n\n" +
                        "• No electrical circuits with Remote Power Supply fixtures (Circuits tab), and\n" +
                        "• No driver types or Remote Power Supply fixture types to compare (Driver Match tab).\n\n" +
                        "Ensure Lighting Fixtures have the 'Remote Power Supply' type parameter checked and that " +
                        "driver (Lighting Device) types are loaded.");
                    return Result.Cancelled;
                }

                var workQueue = new RevitWorkQueue("TurboRPS Error", "TurboRPS Work Queue");
                var operations = new RpsRevitOperations(uidoc);
                var circuitsViewModel = new RpsMainViewModel(circuits, operations, workQueue);

                // "Driver Match" tab — design-time driver↔fixture type comparison, sharing the one
                // work queue. Catalog collected above (API context); Reload re-collects via the queue.
                var matchOps = new DriverMatchOperations(uidoc);
                var matchViewModel = new DriverMatchViewModel(matchCatalog, matchOps, workQueue);

                var shellViewModel = new TurboRpsShellViewModel(circuitsViewModel, matchViewModel);

                var window = new TurboRPSWindow
                {
                    DataContext = shellViewModel
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
                TaskDialog.Show("TurboRPS Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
