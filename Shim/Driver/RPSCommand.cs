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

                if (circuits.Count == 0)
                {
                    TaskDialog.Show("TurboRPS",
                        "No electrical circuits found with Lighting Fixtures that have Remote Power Supply enabled.\n\n" +
                        "Please ensure:\n" +
                        "• Electrical circuits exist in the project\n" +
                        "• Lighting Fixtures are connected to circuits\n" +
                        "• At least one Lighting Fixture has the 'Remote Power Supply' type parameter checked");
                    return Result.Cancelled;
                }

                var workQueue = new RevitWorkQueue("TurboRPS Error", "TurboRPS Work Queue");
                var operations = new RpsRevitOperations(uidoc);
                var viewModel = new RpsMainViewModel(circuits, operations, workQueue);

                var window = new TurboRPSWindow
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
                TaskDialog.Show("TurboRPS Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
