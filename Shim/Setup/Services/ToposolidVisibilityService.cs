#nullable disable
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
/// in place when the Custom override is written. Setting it on the templates too keeps any future
/// manual application of AL_Floor Plan / AL_RCP correct.
///
/// Caller owns the transaction.
/// </summary>
internal static class ToposolidVisibilityService
{
    private static readonly ElementId ToposolidCategoryId =
        new ElementId(BuiltInCategory.OST_Toposolid);

    /// <summary>Hides Toposolid on the firm Floor Plan + RCP view templates, where present.</summary>
    public static void HideOnTemplates(Document doc)
    {
        HideOn(ViewGenerationService.FindTemplate(doc, SetupConstants.FloorPlanViewTemplateName));
        HideOn(ViewGenerationService.FindTemplate(doc, SetupConstants.RcpViewTemplateName));
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
