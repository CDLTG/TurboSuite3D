#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.Filters;

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
/// body below is only a clean stub — overwrite it freely with whatever probe the current investigation needs.
///
/// CURRENT PROBE — "click-to-hide-layer" resolution (TurboName QoL).
/// Question: when you pick a point on a linked-CAD import, does the geometry's GraphicsStyle resolve to a
/// LAYER subcategory (safe to SetCategoryHidden — hides one layer) or to the import's PARENT category
/// (dangerous — would blank the whole DWG)? Especially for geometry inside a nested block drawn on layer 0.
/// Read-only: reports what it WOULD hide, hides nothing.
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

        // ── Roster: every layer subcategory TurboName's layer list would show, keyed by id ──
        var rows = new Dictionary<ElementId, string>();      // subId  → "file :: layer"
        var parentIds = new Dictionary<ElementId, string>(); // catId  → import category (file) name
        var imports = CadLinkResolver.GetLinkedImports(doc, view);
        foreach (var import in imports)
        {
            var cat = import.Category;
            if (cat == null) continue;
            parentIds[cat.Id] = cat.Name;
            foreach (Category sub in cat.SubCategories)
                if (sub != null) rows[sub.Id] = $"{cat.Name} :: {sub.Name}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Doc: {doc.Title}   View: {view.Name} ({view.ViewType})");
        sb.AppendLine($"Revit: {commandData.Application.Application.VersionNumber}");
        sb.AppendLine($"Linked imports in view: {imports.Count}   layer subcategories: {rows.Count}");
        sb.AppendLine();
        sb.AppendLine("Click CAD geometry to probe it. Escape to finish.");
        sb.AppendLine("Try: (1) an ordinary layer line, (2) something inside a nested BLOCK,");
        sb.AppendLine("     (3) anything you know draws on layer 0, (4) text / a hatch.");
        sb.AppendLine(new string('=', 70));

        int n = 0;
        while (true)
        {
            Reference reference;
            try
            {
                reference = uidoc.Selection.PickObject(
                    Autodesk.Revit.UI.Selection.ObjectType.PointOnElement,
                    new ImportInstanceSelectionFilter(doc),
                    $"SPIKE pick #{n + 1} — click linked CAD geometry (Escape to finish)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }

            n++;
            sb.AppendLine();
            sb.AppendLine($"── PICK #{n} ────────────────────────────────────────────");
            try
            {
                sb.AppendLine(ProbeOne(doc, view, reference, rows, parentIds));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  THREW: {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"({n} pick(s) probed — nothing was hidden; this probe is read-only.)");

        string report = sb.ToString();
        string path = Path.Combine(Path.GetTempPath(), "TurboSpike-layer-probe.txt");
        try { File.WriteAllText(path, report); } catch { path = "(could not write temp file)"; }

        var dlg = new TaskDialog("TurboSpike — layer resolution")
        {
            MainInstruction = $"{n} pick(s) probed",
            MainContent = "Full report written to:\n" + path,
            ExpandedContent = report,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dlg.Show();

        return Result.Succeeded;
    }

    // What one pick resolves to: the import, the GeometryObject, its GraphicsStyle, and — the actual question —
    // whether the style's Category is a LAYER subcategory in the roster (safe to hide) or the import's parent
    // category (would blank the whole DWG).
    private static string ProbeOne(Document doc, View view, Reference reference,
        Dictionary<ElementId, string> rows, Dictionary<ElementId, string> parentIds)
    {
        var sb = new StringBuilder();

        if (doc.GetElement(reference.ElementId) is not ImportInstance import)
            return "  picked element is not an ImportInstance (unexpected — filter should prevent this)";

        sb.AppendLine($"  ImportInstance id : {import.Id}");
        sb.AppendLine($"  import.Category   : {import.Category?.Name ?? "(null)"}  id={import.Category?.Id}");

        var geomObj = import.GetGeometryObjectFromReference(reference);
        if (geomObj == null) return sb + "  GetGeometryObjectFromReference → null";

        sb.AppendLine($"  GeometryObject    : {geomObj.GetType().Name}");
        sb.AppendLine($"  GraphicsStyleId   : {geomObj.GraphicsStyleId}");

        if (doc.GetElement(geomObj.GraphicsStyleId) is not GraphicsStyle style)
            return sb + "  GraphicsStyleId does NOT resolve to a GraphicsStyle  → nothing hideable";

        var cat = style.GraphicsStyleCategory;
        sb.AppendLine($"  GraphicsStyle     : \"{style.Name}\"  type={style.GraphicsStyleType}");
        if (cat == null) return sb + "  GraphicsStyleCategory → null  → nothing hideable";

        sb.AppendLine($"  StyleCategory     : \"{cat.Name}\"  id={cat.Id}");
        sb.AppendLine($"    .Parent         : {(cat.Parent == null ? "(null — this is a TOP-LEVEL category)" : $"\"{cat.Parent.Name}\"  id={cat.Parent.Id}")}");

        // ── The verdict the production guard will key off ──
        bool inRoster = rows.TryGetValue(cat.Id, out string rosterLabel);
        bool isParent = parentIds.TryGetValue(cat.Id, out string parentLabel);

        sb.AppendLine($"    in layer roster : {(inRoster ? $"YES → {rosterLabel}" : "no")}");
        sb.AppendLine($"    is import parent: {(isParent ? $"YES → \"{parentLabel}\"  *** would hide the WHOLE DWG ***" : "no")}");

        bool canHide = false, hiddenNow = false;
        try { canHide = view.CanCategoryBeHidden(cat.Id); } catch { }
        try { hiddenNow = view.GetCategoryHidden(cat.Id); } catch { }
        sb.AppendLine($"    CanCategoryBeHidden={canHide}   GetCategoryHidden={hiddenNow}");

        sb.Append("  VERDICT: " + (inRoster
            ? "safe — hides exactly one layer row"
            : isParent
                ? "REJECT — parent category; guard must refuse this pick"
                : "unknown category (not a listed layer, not the import parent) — guard must refuse"));
        return sb.ToString();
    }
}
