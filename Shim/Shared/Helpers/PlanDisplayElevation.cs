using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Where to drop annotation-only devices (a connector + nested generic annotation, no 3D geometry —
/// TurboDriver's power supplies, TurboDMX's decoders/drivers) so they display in the active plan view.
/// PickPoint's Z is the active work plane, which may sit outside the view's primary range: the device
/// then vanishes while its view-owned tags still draw (our standard RCP is based on the floor level but
/// cut at +6' looking up at a +10' ceiling, so the level itself is below the range).
/// </summary>
public static class PlanDisplayElevation
{
    /// <summary>
    /// Absolute elevation at which to drop annotation-only devices so they display in the
    /// active plan view WITHOUT bleeding into the companion view. Placing at the cut plane
    /// isn't safe: an RCP cut (e.g. +6') often falls inside a floor plan's range, so a device
    /// placed in one view shows up in the other. Instead we go to the range extreme that
    /// points away from the companion view — RCPs look up, so the TOP clip plane (near the
    /// ceiling, above a floor plan's top); floor plans look down, so the BOTTOM clip plane
    /// (near the floor, below an RCP's bottom). Both are primary-range boundaries, so the
    /// device is still guaranteed visible in its own view. Null for views with no resolvable
    /// range (drafting/3D/section).
    ///
    /// This is an ABSOLUTE, internal-origin elevation (model coordinates) — the frame every
    /// LocationPoint.Z uses — so it must be built from Level.ProjectElevation, NOT Level.Elevation.
    /// Level.Elevation is measured from the project's *elevation base*, which a survey/shared/
    /// relocated datum can inflate away from the internal origin (verified in-model via TurboSpike:
    /// a level reading .Elevation = 1346' but .ProjectElevation = -13.5', with all geometry sitting
    /// in the -13.5' frame). Using .Elevation there shoved drivers ~1360' into the sky. The two are
    /// equal in an un-relocated project, so ProjectElevation is correct universally.
    /// </summary>
    public static double? Of(Document doc, View view)
    {
        var plan = view as ViewPlan;
        if (plan == null)
            return null;

        PlanViewPlane whichPlane = plan.ViewType == ViewType.CeilingPlan
            ? PlanViewPlane.TopClipPlane
            : PlanViewPlane.BottomClipPlane;

        PlanViewRange range = plan.GetViewRange();

        // Resolve one range plane to an absolute internal-origin elevation, or null if its level
        // can't be resolved. The plane's level can be a real level OR one of Revit's "Level
        // Above"/"Level Below" sentinel ids (e.g. an RCP with Top = Level Above, offset 0);
        // sentinels don't resolve via GetElement, so map them to the neighbouring level.
        // ProjectElevation (internal origin), not Elevation (elevation base) — see method summary.
        double? ResolvePlane(PlanViewPlane p)
        {
            ElementId levelId = range.GetLevelId(p);
            Level? level;
            if (levelId.Equals(PlanViewRange.LevelAbove))
                level = AdjacentLevel(doc, plan.GenLevel, above: true);
            else if (levelId.Equals(PlanViewRange.LevelBelow))
                level = AdjacentLevel(doc, plan.GenLevel, above: false);
            else
                level = doc.GetElement(levelId) as Level;

            return level != null ? level.ProjectElevation + range.GetOffset(p) : (double?)null;
        }

        // Primary: the range extreme pointing away from the companion view. If that plane's level
        // can't be resolved — the classic case being a top-floor RCP whose Top is "Level Above"
        // with no level above — degrade WITHIN the plan rather than to the raw picked Z (which is
        // just wherever the work plane sits, out of range). The cut plane always lies inside the
        // primary range, so it's guaranteed visible; GenLevel is the last-ditch anchor.
        return ResolvePlane(whichPlane)
            ?? ResolvePlane(PlanViewPlane.CutPlane)
            ?? (plan.GenLevel != null
                    ? plan.GenLevel.ProjectElevation + range.GetOffset(whichPlane)
                    : (double?)null);
    }

    /// <summary>
    /// The nearest level directly above (or below) the given level by elevation, or null if
    /// there is none. Used to resolve view-range "Level Above"/"Level Below" sentinels.
    /// Ordered by ProjectElevation (internal origin) so the neighbour search matches the frame
    /// <see cref="Of"/> builds in — a per-level elevation base can otherwise reorder .Elevation.
    /// </summary>
    private static Level? AdjacentLevel(Document doc, Level? from, bool above)
    {
        if (from == null)
            return null;

        double baseElev = from.ProjectElevation;
        const double Tol = 1e-6;
        Level? best = null;
        foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
        {
            if (!(el is Level lvl))
                continue;
            double e = lvl.ProjectElevation;
            if (above)
            {
                if (e > baseElev + Tol && (best == null || e < best.ProjectElevation))
                    best = lvl;
            }
            else
            {
                if (e < baseElev - Tol && (best == null || e > best.ProjectElevation))
                    best = lvl;
            }
        }
        return best;
    }
}
