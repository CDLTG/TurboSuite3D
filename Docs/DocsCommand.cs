using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Docs.ViewModels;
using TurboSuite.Docs.Views;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs;

[Transaction(TransactionMode.Manual)]
public class DocsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document? doc = uidoc?.Document;

        if (doc == null)
        {
            TaskDialog.Show("TurboDocs", "No active document.");
            return Result.Failed;
        }

        // Collect cut sheet fixture data
        var symbolIds = new HashSet<ElementId>();
        var cutSheetFixtures = new List<FixtureSpecModel>();

        var instances = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_LightingFixtures)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in instances)
        {
            var symbol = fi.Symbol;
            if (symbol == null || !symbolIds.Add(symbol.Id)) continue;

            var tmParam = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string typeMark = (tmParam is { HasValue: true }) ? tmParam.AsString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(typeMark)) continue;

            var urlParam = symbol.LookupParameter("Data Sheet URL");
            string url = (urlParam is { HasValue: true }) ? urlParam.AsString() ?? "" : "";

            var catParts = new List<string>();
            for (int c = 1; c <= 6; c++)
            {
                var catParam = symbol.LookupParameter($"Catalog Number{c}");
                string val = (catParam is { HasValue: true }) ? catParam.AsString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(val)) catParts.Add(val.Trim());
            }
            string catalogNumber = string.Join(" | ", catParts);

            cutSheetFixtures.Add(new FixtureSpecModel
            {
                TypeMark = typeMark,
                FamilyName = symbol.FamilyName,
                DataSheetUrl = url,
                CatalogNumber = catalogNumber,
                SymbolId = symbol.Id
            });
        }

        cutSheetFixtures.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, System.StringComparison.OrdinalIgnoreCase));

        // Collect schedule fixture data
        var scheduleFixtures = ScheduleCollectorService.Collect(doc);

        // Collect RPS (remote power supply) data
        var (rpsScheduleItems, rpsInstances, rpsCutSheetFixtures) = RPSCollectorService.Collect(doc);

        // Collect load schedule circuit data
        var loadsCircuits = LoadsCollectorService.Collect(doc);

        // Collect panel schedule data
        PanelScheduleData? panelData = null;
        try { panelData = PanelScheduleCollectorService.Collect(doc); }
        catch { /* Panel data unavailable — tab will show empty state */ }

        // Collect counts (fixture quantities) data
        var countsFixtures = CountsCollectorService.Collect(doc);

        // Collect BOM data
        BomData? bomData = null;
        try { bomData = BomCollectorService.Collect(doc); }
        catch { /* BOM data unavailable — tab will show empty state */ }

        bool hasPanelData = panelData?.Allocation?.AllPanels.Count > 0;
        bool hasBomData = bomData?.Items.Count > 0;
        bool hasCountsData = countsFixtures.Count > 0;
        if (cutSheetFixtures.Count == 0 && scheduleFixtures.Count == 0 && rpsScheduleItems.Count == 0 && loadsCircuits.Count == 0 && !hasPanelData && !hasBomData && !hasCountsData)
        {
            TaskDialog.Show("TurboDocs", "No lighting fixture types found in the active document.");
            return Result.Cancelled;
        }

        // Collect notes from key schedules
        var generalNotes = NotesCollectorService.CollectGeneralNotes(doc);
        var controlNotes = NotesCollectorService.CollectControlNotes(doc);

        string projectName = doc.ProjectInformation?.Name ?? "Untitled Project";
        string projectNumber = doc.ProjectInformation?.Number ?? "";

        var viewModel = new DocsViewModel(cutSheetFixtures, rpsCutSheetFixtures, projectName, projectNumber);
        viewModel.ScheduleVM.LoadFixtures(scheduleFixtures);
        viewModel.PowerSuppliesVM.LoadData(rpsScheduleItems, rpsInstances);
        viewModel.LoadsVM.LoadCircuits(loadsCircuits);
        if (panelData != null)
            viewModel.PanelScheduleVM.LoadData(panelData);
        if (bomData != null)
            viewModel.BomVM.LoadData(bomData);
        viewModel.NotesVM.LoadNotes(generalNotes, controlNotes);
        viewModel.CountsVM.LoadData(countsFixtures);

        var window = new TurboDocsWindow { DataContext = viewModel };
        var helper = new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        window.ShowDialog();

        return Result.Succeeded;
    }
}
