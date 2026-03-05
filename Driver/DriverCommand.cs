#nullable disable
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Driver.ViewModels;
using TurboSuite.Driver.Services;
using TurboSuite.Driver.Views;

namespace TurboSuite.Driver
{
    /// <summary>
    /// External Command that launches the TurboDriver window
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DriverCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("TurboDriver", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboDriver", "Please close any active transactions before opening TurboDriver.");
                    return Result.Failed;
                }

                CircuitCollectorService circuitService = new CircuitCollectorService();
                FamilyTypeCollectorService typeService = new FamilyTypeCollectorService();

                var circuits = circuitService.GetFilteredCircuits(doc);
                var availableTypes = typeService.GetAllLightingDeviceTypes(doc);
                var driverCandidates = typeService.GetDriverCandidates(availableTypes);

                if (circuits.Count == 0)
                {
                    TaskDialog.Show("TurboDriver",
                        "No electrical circuits found with Lighting Fixtures that have Remote Power Supply enabled.\n\n" +
                        "Please ensure:\n" +
                        "• Electrical circuits exist in the project\n" +
                        "• Lighting Fixtures are connected to circuits\n" +
                        "• At least one Lighting Fixture has the 'Remote Power Supply' type parameter checked");
                    return Result.Cancelled;
                }

                MainViewModel viewModel = new MainViewModel(doc, uidoc, circuits, availableTypes, driverCandidates);

                TurboDriverWindow window = new TurboDriverWindow
                {
                    DataContext = viewModel
                };

                var revitHandle = commandData.Application.MainWindowHandle;
                var helper = new System.Windows.Interop.WindowInteropHelper(window) { Owner = revitHandle };

                bool? result = window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboDriver Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
