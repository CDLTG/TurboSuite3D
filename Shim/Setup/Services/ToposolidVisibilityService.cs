#nullable disable
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Turns the Toposolid model category off on the firm view templates and on each generated host
/// view, so the linked architectural Toposolid stops drawing in the lighting set.
///
/// Why this lives in TurboSetup and not the template: the firm template originates in Revit 2022,
/// which predates the Toposolid category, so its view templates can't express "Toposolid off." Jobs
/// (2024+) link arch models that carry Toposolids the lighting set never wants to see. TurboSetup
/// runs inside the 2024+ job, where OST_Toposolid is a real, controllable category — so it can set
/// what the 2022 template couldn't.
///
/// This sets HOST-view category visibility. The linked Toposolid follows because (a) a freshly
/// created view defaults its link display to "By host view", and (b) the firm link hybrid resolves
/// model categories to the host view (ObjectStyles = ByHostView) — so the host-off state is already
/// in place when the Custom override is written.
///
/// The template sweep (<see cref="HideOnTemplates"/>) covers every firm lighting template
/// (AL_ prefix), not just the generated Floor Plan / RCP. This reaches templates TurboSetup never
/// creates views for — chiefly AL_Section, which is auto-applied ("applied but not held") to new
/// section views and sets RVT Links to "By Host View". Because "By Host View" resolves to the
/// section view's own model-category visibility, and AL_Section controls Model Categories, hiding
/// Toposolid on the template carries a Toposolid-off state into every section drawn later.
///
/// Caller owns the transaction.
/// </summary>
internal static class ToposolidVisibilityService
{
    private static readonly ElementId ToposolidCategoryId =
        new ElementId(BuiltInCategory.OST_Toposolid);

    /// <summary>
    /// Hides Toposolid on every firm lighting view template (name starts with
    /// <see cref="SetupConstants.LightingTemplatePrefix"/>), where the category is controllable.
    /// </summary>
    public static void HideOnTemplates(Document doc)
    {
        var templates = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate
                && v.Name.StartsWith(SetupConstants.LightingTemplatePrefix,
                    System.StringComparison.OrdinalIgnoreCase));

        foreach (var template in templates)
            HideOn(template);
    }

    /// <summary>
    /// Hides Toposolid on a single view (or view template). No-op when the view is null or the
    /// category can't be controlled in that view (so a 2024 RCP/odd view never throws).
    /// </summary>
    public static void HideOn(View view)
    {
        if (view == null) return;
        if (!view.CanCategoryBeHidden(ToposolidCategoryId)) return;
        view.SetCategoryHidden(ToposolidCategoryId, true);
    }
}
