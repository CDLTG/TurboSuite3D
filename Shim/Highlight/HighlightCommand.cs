using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Highlight;

/// <summary>
/// TurboHighlight — one keystroke toggles the room highlight in the active plan view.
///
///   3D job (any RVT link) → V/G Spaces: every press turns Spaces on. Color Fill / Interior / Reference
///                           all off (or Spaces was off) → all on; mixed or all on → all off.
///   2D job (no RVT link)  → V/G Filters "Highlight - Rooms": force Enable Filter on and flip Visibility
///                           (added to the view, visible, if the template hasn't put it there).
///
/// The 3D/2D seam is "any RevitLinkInstance", loaded or not — unlike TurboSetup, toggling V/G never
/// touches the link document, so an unloaded arch link must still read as 3D.
///
/// A view with a template applied is refused: firm templates force the V/G values, and the workflow is
/// to unassign the template before editing — a template still on the view is intentional.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class HighlightCommand : IExternalCommand
{
    /// <summary>The project-template filter the 2D branch toggles.</summary>
    private const string RoomFilterName = "Highlight - Rooms";

    // Spike-confirmed (Revit 2025): the V/G checkbox names map to the *Visibility variants for Interior and
    // Reference — OST_MEPSpaceInteriorFill / OST_MEPSpaceReference resolve to no category.
    private static readonly BuiltInCategory[] SpaceSubcategories =
    {
        BuiltInCategory.OST_MEPSpaceColorFill,
        BuiltInCategory.OST_MEPSpaceInteriorFillVisibility,
        BuiltInCategory.OST_MEPSpaceReferenceVisibility,
    };

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboHighlight", "No active document found.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        if (!IsPlanView(view))
        {
            TaskDialog.Show("TurboHighlight", "Run TurboHighlight from a Floor Plan or Reflected Ceiling Plan view.");
            return Result.Cancelled;
        }

        if (view.ViewTemplateId != ElementId.InvalidElementId)
        {
            string templateName = doc.GetElement(view.ViewTemplateId)?.Name ?? "a view template";
            TaskDialog.Show("TurboHighlight",
                $"This view has '{templateName}' applied.\n\nUnassign the view template to toggle the room highlight.");
            return Result.Cancelled;
        }

        bool is3D = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .GetElementCount() > 0;

        try
        {
            return is3D ? ToggleSpaces(doc, view) : ToggleRoomFilter(doc, view);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("TurboHighlight", $"Could not toggle the room highlight:\n{ex.Message}");
            return Result.Failed;
        }
    }

    private static Result ToggleSpaces(Document doc, View view)
    {
        ElementId spacesId = new ElementId(BuiltInCategory.OST_MEPSpaces);
        ElementId[] subIds = SpaceSubcategories.Select(b => new ElementId(b)).ToArray();
        ElementId[] allIds = new[] { spacesId }.Concat(subIds).ToArray();

        if (allIds.Any(id => !view.CanCategoryBeHidden(id)))
        {
            TaskDialog.Show("TurboHighlight", "Spaces visibility can't be changed in this view.");
            return Result.Cancelled;
        }

        // Spaces off hides the subcategories whatever their own checkboxes say, so it reads as "all off" —
        // otherwise a press with Spaces off and subcategories checked would show nothing.
        bool showSubs = view.GetCategoryHidden(spacesId) || subIds.All(id => view.GetCategoryHidden(id));

        using (Transaction tx = new Transaction(doc, "TurboHighlight — Spaces"))
        {
            tx.Start();
            view.SetCategoryHidden(spacesId, false);
            foreach (ElementId id in subIds)
                view.SetCategoryHidden(id, !showSubs);
            tx.Commit();
        }

        return Result.Succeeded;
    }

    private static Result ToggleRoomFilter(Document doc, View view)
    {
        FilterElement? filter = new FilteredElementCollector(doc)
            .OfClass(typeof(FilterElement))
            .Cast<FilterElement>()
            .FirstOrDefault(f => f.Name == RoomFilterName);

        if (filter == null)
        {
            TaskDialog.Show("TurboHighlight", $"No '{RoomFilterName}' filter found in this project.");
            return Result.Cancelled;
        }

        using (Transaction tx = new Transaction(doc, "TurboHighlight — Rooms filter"))
        {
            tx.Start();
            if (!view.IsFilterApplied(filter.Id))
            {
                // Not on the view yet — the first press shows the highlight.
                view.AddFilter(filter.Id);
                view.SetFilterVisibility(filter.Id, true);
            }
            else
            {
                view.SetFilterVisibility(filter.Id, !view.GetFilterVisibility(filter.Id));
            }
            view.SetIsFilterEnabled(filter.Id, true);
            tx.Commit();
        }

        return Result.Succeeded;
    }

    private static bool IsPlanView(View view) =>
        view != null &&
        (view.ViewType == ViewType.FloorPlan ||
         view.ViewType == ViewType.CeilingPlan ||
         view.ViewType == ViewType.EngineeringPlan ||
         view.ViewType == ViewType.AreaPlan);
}
