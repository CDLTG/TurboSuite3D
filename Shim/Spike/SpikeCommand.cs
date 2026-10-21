#nullable disable
using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench.
///
/// PURPOSE: an always-available (in dev) ribbon button whose <see cref="Execute"/> body is meant to be
/// SWAPPED per-investigation. When you need to know something about the running model before writing
/// targeted code — what a parameter's StorageType actually is, whether an API member exists on this
/// Revit version, what a family's connectors/geometry look like — drop a probe here, build, and read
/// the dialog.
///
/// STATE: rides the shared <c>ExperimentalCommandsEnabled</c> gate in <see cref="App.TurboSuiteApplication"/>,
/// so it surfaces every dev session and is gated off in shipped builds — it never reaches production users.
///
/// Keep this ReadOnly and side-effect-free by default. If a probe needs to write, wrap it in a Transaction
/// and change the attribute for the duration of that spike — then revert. This file is scratch space; the
/// body below is only a sensible starting probe, not something to preserve.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        var sb = new StringBuilder();
        sb.AppendLine($"Revit {commandData.Application.Application.VersionNumber} — view: {view.Name} ({view.ViewType})");
        sb.AppendLine($"ViewTemplateId: {view.ViewTemplateId}");

        // Probe 1: the Spaces category's subcategories as V/G lists them — name ↔ BuiltInCategory ↔ hidden state.
        sb.AppendLine();
        sb.AppendLine("── Spaces subcategories (Category.SubCategories) ──");
        Category spaces = Category.GetCategory(doc, BuiltInCategory.OST_MEPSpaces);
        if (spaces == null)
        {
            sb.AppendLine("  OST_MEPSpaces category not found");
        }
        else
        {
            sb.AppendLine($"  [parent] '{spaces.Name}'  bic={spaces.BuiltInCategory}  id={spaces.Id}  " +
                          $"canHide={view.CanCategoryBeHidden(spaces.Id)}  hidden={view.GetCategoryHidden(spaces.Id)}");
            foreach (Category sub in spaces.SubCategories.Cast<Category>().OrderBy(c => c.Name))
            {
                sb.AppendLine($"  '{sub.Name}'  bic={sub.BuiltInCategory}  id={sub.Id}  " +
                              $"canHide={view.CanCategoryBeHidden(sub.Id)}  hidden={view.GetCategoryHidden(sub.Id)}");
            }
        }

        // Probe 2: every OST_MEPSpace* enum value — which resolve to a real category, and under which parent.
        sb.AppendLine();
        sb.AppendLine("── OST_MEPSpace* enum values ──");
        foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)).Cast<BuiltInCategory>()
                     .Where(b => b.ToString().StartsWith("OST_MEPSpace", StringComparison.Ordinal))
                     .Distinct())
        {
            Category c = null;
            try { c = Category.GetCategory(doc, bic); } catch { }
            sb.AppendLine(c == null
                ? $"  {bic}: (no category)"
                : $"  {bic}: '{c.Name}'  parent='{c.Parent?.Name}'");
        }

        // Probe 3: the 2D filter — element type, applied/enabled/visible on this view.
        sb.AppendLine();
        sb.AppendLine("── 'Highlight - Rooms' filter ──");
        FilterElement filter = new FilteredElementCollector(doc)
            .OfClass(typeof(FilterElement))
            .Cast<FilterElement>()
            .FirstOrDefault(f => f.Name == "Highlight - Rooms");
        if (filter == null)
        {
            sb.AppendLine("  not found");
        }
        else
        {
            sb.AppendLine($"  type={filter.GetType().Name}  id={filter.Id}");
            bool applied = view.IsFilterApplied(filter.Id);
            sb.AppendLine($"  applied={applied}");
            if (applied)
                sb.AppendLine($"  enabled={view.GetIsFilterEnabled(filter.Id)}  visible={view.GetFilterVisibility(filter.Id)}");
        }

        TaskDialog.Show("TurboSpike", sb.ToString());
        return Result.Succeeded;
    }
}
