using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Shim adapter from a Revit view to the pure <see cref="PlanScale"/>. Take it from the view the
/// command places into — TurboSuite's output is view-owned, so the factor is per view.
/// </summary>
public static class PlanScaleHelper
{
    public static PlanScale Of(View view) => PlanScale.FromViewScale(view.Scale);
}
