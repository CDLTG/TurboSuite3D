using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Cuts.Models;
using TurboSuite.Cuts.ViewModels;
using TurboSuite.Cuts.Views;

namespace TurboSuite.Cuts;

[Transaction(TransactionMode.Manual)]
public class CutsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document? doc = uidoc?.Document;

        if (doc == null)
        {
            TaskDialog.Show("TurboCuts", "No active document.");
            return Result.Failed;
        }

        // Collect unique FamilySymbols from placed lighting fixtures
        var symbolIds = new HashSet<ElementId>();
        var fixtures = new List<FixtureSpecModel>();

        var instances = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_LightingFixtures)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in instances)
        {
            var symbol = fi.Symbol;
            if (symbol == null || !symbolIds.Add(symbol.Id)) continue;

            var urlParam = symbol.LookupParameter("Data Sheet URL");
            string url = (urlParam is { HasValue: true }) ? urlParam.AsString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url)) continue;

            var tmParam = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string typeMark = (tmParam is { HasValue: true }) ? tmParam.AsString() ?? "" : "";

            fixtures.Add(new FixtureSpecModel
            {
                TypeMark = typeMark,
                FamilyName = symbol.FamilyName,
                DataSheetUrl = url,
                SymbolId = symbol.Id
            });
        }

        fixtures.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, System.StringComparison.OrdinalIgnoreCase));

        if (fixtures.Count == 0)
        {
            TaskDialog.Show("TurboCuts", "No lighting fixture types with a Data Sheet URL found.");
            return Result.Cancelled;
        }

        string projectName = doc.ProjectInformation?.Name ?? "Untitled Project";

        var viewModel = new TurboCutsViewModel(fixtures, projectName);
        var window = new TurboCutsWindow { DataContext = viewModel };
        var helper = new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        window.ShowDialog();

        return Result.Succeeded;
    }
}
