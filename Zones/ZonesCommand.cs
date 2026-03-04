#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Zones.Services;
using TurboSuite.Zones.ViewModels;
using TurboSuite.Zones.Views;

namespace TurboSuite.Zones
{
    [Transaction(TransactionMode.Manual)]
    public class ZonesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
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
                var panelCatalogNumbers = collectorService.GetPanelCatalogNumbers(doc);
                var viewModel = new ZonesMainViewModel(doc, circuits,
                    keypadCount, twoGangKeypadCount, hybridRepeaterCount, hybridRepeaterPartNumber,
                    panelCatalogNumbers);

                var window = new TurboZonesWindow
                {
                    DataContext = viewModel
                };

                var revitHandle = commandData.Application.MainWindowHandle;
                var helper = new WindowInteropHelper(window) { Owner = revitHandle };

                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboZones Error", $"An error occurred:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}
