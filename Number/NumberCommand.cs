#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Services;
using TurboSuite.Number.ViewModels;
using TurboSuite.Number.Views;

namespace TurboSuite.Number
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class NumberCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
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

                var viewModel = new NumberMainViewModel(doc, circuits, keypads, powerSupplies, collectorService);

                var window = new TurboNumberWindow
                {
                    DataContext = viewModel
                };

                var revitHandle = commandData.Application.MainWindowHandle;
                var helper = new WindowInteropHelper(window) { Owner = revitHandle };

                window.ShowDialog();

                var scheduleView = viewModel.CircuitTab.ScheduleViewToOpen;
                if (scheduleView != null)
                    uidoc.RequestViewChange(scheduleView);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboNumber Error", $"An error occurred:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}
