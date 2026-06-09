#nullable disable
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Copies a linked architectural view's view range onto a generated host view. Every plane is
/// re-anchored to the host view's Associated Level, with the offset recomputed so the plane keeps
/// the architect's true (absolute) elevation — correct whether the architect anchored to Associated
/// Level or to some other level (e.g. a foundation level) that TurboSetup didn't copy.
///
/// The host link display then follows the host view range (RVT Link ViewRange = ByHostView), making
/// the host range the single source of truth: adjust it later and the link moves with it.
///
/// <see cref="PlanViewRange"/> is version-agnostic, so this lives in shared source.
/// </summary>
internal static class ViewRangeService
{
    private const double Tol = 1e-6;

    // Top / Cut / Bottom / View Depth — the four planes shown in the View Range dialog.
    private static readonly PlanViewPlane[] Planes =
    {
        PlanViewPlane.TopClipPlane,
        PlanViewPlane.CutPlane,
        PlanViewPlane.BottomClipPlane,
        PlanViewPlane.ViewDepthPlane,
    };

    /// <summary>
    /// Applies <paramref name="linkedView"/>'s view range to <paramref name="hostView"/>.
    /// Returns false (no change) if either view has no usable range/level.
    /// </summary>
    public static bool CopyFromLinkedView(ViewPlan hostView, ViewPlan linkedView, Document linkDoc)
    {
        PlanViewRange src = linkedView.GetViewRange();
        Level archViewLevel = linkedView.GenLevel;
        if (src == null || archViewLevel == null)
            return false;

        double archViewElev = archViewLevel.Elevation;
        PlanViewRange dst = hostView.GetViewRange();

        foreach (var plane in Planes)
        {
            ElementId refId = src.GetLevelId(plane);
            double offset = src.GetOffset(plane);

            // "Unlimited" (typical View Depth) has no level/offset to translate — keep it.
            if (refId == PlanViewRange.Unlimited)
            {
                dst.SetLevelId(plane, PlanViewRange.Unlimited);
                continue;
            }

            // Absolute elevation of the level the architect's plane is measured from.
            double anchorElev;
            if (refId == PlanViewRange.Current)
                anchorElev = archViewElev;
            else if (refId == PlanViewRange.LevelAbove)
                anchorElev = NeighborElevation(linkDoc, archViewElev, above: true) ?? archViewElev;
            else if (refId == PlanViewRange.LevelBelow)
                anchorElev = NeighborElevation(linkDoc, archViewElev, above: false) ?? archViewElev;
            else
                anchorElev = (linkDoc.GetElement(refId) as Level)?.Elevation ?? archViewElev;

            // Re-express the plane as "host Associated Level + offset" preserving its true height.
            // (Level differences cancel the link placement transform, so this is Z-offset safe.)
            dst.SetLevelId(plane, PlanViewRange.Current);
            dst.SetOffset(plane, anchorElev + offset - archViewElev);
        }

        hostView.SetViewRange(dst);
        return true;
    }

    /// <summary>Elevation of the architect level immediately above/below <paramref name="elev"/>, or null.</summary>
    private static double? NeighborElevation(Document linkDoc, double elev, bool above)
    {
        var levels = new FilteredElementCollector(linkDoc).OfClass(typeof(Level)).Cast<Level>();
        return above
            ? levels.Where(l => l.Elevation > elev + Tol).OrderBy(l => l.Elevation).FirstOrDefault()?.Elevation
            : levels.Where(l => l.Elevation < elev - Tol).OrderByDescending(l => l.Elevation).FirstOrDefault()?.Elevation;
    }
}
