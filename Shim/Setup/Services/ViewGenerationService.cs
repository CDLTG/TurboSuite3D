#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Setup.Models;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Creates Floor Plan / RCP views on the copied host levels and bakes in the firm view templates.
/// Caller owns the transaction.
///
/// Templates are applied via <see cref="View.ApplyViewTemplateParameters"/> — a one-shot copy of the
/// template's settings that leaves the view's ViewTemplateId as None (editable). This mirrors the
/// manual workflow ("assign the template to get the baseline, then set it back to none") and is
/// essential: if the template instead stayed *associated* and controlled "RVT Links", the per-view
/// link override would be template-owned and SetLinkOverrides would fail with "the view does not
/// support link graphical overrides". Applying-then-detaching keeps the view free to take overrides.
/// </summary>
internal static class ViewGenerationService
{
    /// <summary>The created host view, paired with the planned-view record that produced it.</summary>
    internal sealed class CreatedView
    {
        public ViewPlan View { get; set; }
        public PlannedView Planned { get; set; }
    }

    /// <summary>
    /// Creates each planned view whose name does not already exist in the host. Existing-name
    /// views are skipped entirely (not created and not modified). Returns the views actually
    /// created; <paramref name="skippedExisting"/> reports how many were skipped.
    /// </summary>
    public static List<CreatedView> CreateViews(
        Document hostDoc,
        IList<PlannedView> plannedViews,
        IDictionary<ElementId, Level> sourceToHostLevel,
        out int skippedExisting)
    {
        skippedExisting = 0;
        var created = new List<CreatedView>();

        ElementId floorVft = FirstViewFamilyType(hostDoc, ViewFamily.FloorPlan);
        ElementId ceilingVft = FirstViewFamilyType(hostDoc, ViewFamily.CeilingPlan);

        View floorTemplate = FindTemplate(hostDoc, SetupConstants.FloorPlanViewTemplateName);
        View rcpTemplate = FindTemplate(hostDoc, SetupConstants.RcpViewTemplateName);

        // Existing host view names — case-insensitive skip set.
        var existingNames = new HashSet<string>(
            new FilteredElementCollector(hostDoc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v => v.Name),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var planned in plannedViews)
        {
            if (existingNames.Contains(planned.ViewName))
            {
                skippedExisting++;
                continue;
            }

            if (!sourceToHostLevel.TryGetValue(planned.SourceLevelId, out var hostLevel) || hostLevel == null)
                continue;

            ElementId vft = planned.Kind == ViewKind.Floor ? floorVft : ceilingVft;
            if (vft == null || vft == ElementId.InvalidElementId)
                continue;

            var view = ViewPlan.Create(hostDoc, vft, hostLevel.Id);
            view.Name = planned.ViewName;

            // Bake in the template settings as a one-shot baseline, then leave the view free
            // (ViewTemplateId stays None) so link graphics overrides can be applied afterward.
            View template = planned.Kind == ViewKind.Floor ? floorTemplate : rcpTemplate;
            if (template != null)
                view.ApplyViewTemplateParameters(template);

            created.Add(new CreatedView { View = view, Planned = planned });
            existingNames.Add(planned.ViewName);
        }

        return created;
    }

    /// <summary>First view template matching <paramref name="name"/>, or null.</summary>
    public static View FindTemplate(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && v.Name == name);
    }

    /// <summary>Id of the view template matching <paramref name="name"/>, or null.</summary>
    public static ElementId FindTemplateId(Document doc, string name) => FindTemplate(doc, name)?.Id;

    // Multiple FloorPlan/CeilingPlan VFTs can exist; default to the first found.
    private static ElementId FirstViewFamilyType(Document doc, ViewFamily family)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == family)?.Id;
    }
}
