using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Runtime room detection sourced from project-owned <see cref="Space"/>s — the owned layer that replaced
/// trusting the architect's linked Rooms. Architect Rooms are NOT consulted at runtime; they only seed space
/// *names* (see <c>SpaceNamingService</c>). A fixture resolves to the Space whose plan footprint contains it,
/// disambiguated across stacked storeys by <see cref="RoomBandSelector"/>.
///
/// Mode-invariant by design, and this is the whole reason Spaces are usable here: a room-bounding toposolid
/// (always welded into the arch link) collapses grade-level Space *volumes* under Areas-and-Volumes — but it
/// damages only volume, never the 2D footprint. Measured on the prototype job, a Space's <c>Area</c> and
/// boundary loops were identical under Areas-only and Areas-and-Volumes for every space, including
/// volume-collapsed ones. So detection reads only what survives: the Space's 2D boundary
/// (<see cref="SpatialElement.GetBoundarySegments"/>) and its <see cref="Level"/> elevation. It never touches
/// Space.Volume, IsPointInSpace, or the 3D bbox — all corrupted by the collapse — so it stays correct under
/// the project's normal Areas-and-Volumes mode (required by ElumTools), with no setting change.
///
/// Cascade: Space → 2D "Room Region" fallback (pure-2D drafting jobs) → null (caller's Room Override).
/// </summary>
public static class SpaceRoomFinderService
{
    /// <summary>Plan prefilter half-width (ft). Generous; the real test is point-in-polygon on the boundary.</summary>
    private const double Margin = 0.5;

    // Spaces expose their name/number through the same built-in parameters as Rooms. Read the NAME parameter
    // directly (never Space.Name, which returns "Name Number" combined). Centralized so the one place that
    // knows the parameter is here. [Verify on the live model: ROOM_NAME/ROOM_NUMBER resolve on Spaces.]
    public static string ReadSpaceName(Space space) =>
        space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

    public static string ReadSpaceNumber(Space space) =>
        space.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";

    /// <summary>Writes the space's display name (the ROOM_NAME parameter). Must be called inside a
    /// transaction. Returns false if the parameter is missing or read-only.</summary>
    public static bool WriteSpaceName(Space space, string name)
    {
        Parameter p = space.get_Parameter(BuiltInParameter.ROOM_NAME);
        if (p == null || p.IsReadOnly) return false;
        return p.Set(name);
    }

    private sealed class SpaceEntry
    {
        public Space Space = null!;
        public double MinX, MaxX, MinY, MaxY;   // plan prefilter (host coords)
        public double FloorZ;                    // Space.Level.ProjectElevation — banding key
        public List<List<XYZ>> Loops = new();    // [0] outer, [1..] holes; tessellated, host coords
    }

    /// <summary>
    /// Pre-collects all placed Spaces once for batch fixture lookups. Create once per collect, then discard.
    /// Optionally accepts a <see cref="RegionRoomLookupService"/> for the 2D drafting fallback.
    /// </summary>
    public class SpaceLookupCache
    {
        private readonly List<SpaceEntry> _spaces;
        private readonly RegionRoomLookupService? _regionFallback;

        public SpaceLookupCache(Document doc, RegionRoomLookupService? regionFallback = null)
        {
            _regionFallback = regionFallback;
            _spaces = CollectSpaces(doc);
        }

        public Space? FindSpace(FamilyInstance fixture)
        {
            if (!LinkedRoomFinderService.TryGetFixturePoint(fixture, out XYZ point))
                return null;
            return FindSpaceAtPoint(point);
        }

        internal Space? FindSpaceAtPoint(XYZ point)
        {
            var candidates = new List<Space>();
            var floorZs = new List<double>();

            foreach (SpaceEntry e in _spaces)
            {
                if (point.X < e.MinX - Margin || point.X > e.MaxX + Margin ||
                    point.Y < e.MinY - Margin || point.Y > e.MaxY + Margin)
                    continue;
                if (PointInLoops(e.Loops, point))
                {
                    candidates.Add(e.Space);
                    floorZs.Add(e.FloorZ);
                }
            }

            int idx = RoomBandSelector.SelectBandIndex(floorZs, point.Z);
            return idx >= 0 ? candidates[idx] : null;
        }

        /// <summary>
        /// Room name for a fixture: the containing Space's name, else the 2D "Room Region" fallback,
        /// else null (the caller then applies its Room Override).
        /// </summary>
        public string? FindRoomName(FamilyInstance fixture)
        {
            Space? space = FindSpace(fixture);
            if (space != null)
            {
                string name = ReadSpaceName(space);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            if (_regionFallback != null && LinkedRoomFinderService.TryGetFixturePoint(fixture, out XYZ point))
                return _regionFallback.FindRoomName(point);

            return null;
        }
    }

    private static List<SpaceEntry> CollectSpaces(Document doc)
    {
        // Center boundary location so the footprint reaches to wall centerlines — this is what lets a
        // wall-recessed keypad (at the wall center, ~half a wall outside the finish face) resolve, mirroring
        // the old Room IsPointInRoom (centerline) behavior that already handled keypads.
        var opts = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center
        };

        var entries = new List<SpaceEntry>();
        var spaces = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType()
            .Cast<Space>();

        foreach (Space space in spaces)
        {
            if (space.Area <= 0) continue;              // unplaced / unenclosed
            Level level = space.Level;
            if (level == null) continue;

            List<List<XYZ>> loops = TessellateBoundary(space, opts);
            if (loops.Count == 0 || loops[0].Count < 3) continue;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (XYZ pt in loops[0])
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            entries.Add(new SpaceEntry
            {
                Space = space,
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                FloorZ = level.ProjectElevation,
                Loops = loops
            });
        }
        return entries;
    }

    private static List<List<XYZ>> TessellateBoundary(Space space, SpatialElementBoundaryOptions opts)
    {
        var loops = new List<List<XYZ>>();
        IList<IList<BoundarySegment>>? segLoops = null;
        try { segLoops = space.GetBoundarySegments(opts); } catch { /* fall through */ }
        if (segLoops == null) return loops;

        foreach (IList<BoundarySegment> loop in segLoops)
        {
            var pts = new List<XYZ>();
            foreach (BoundarySegment seg in loop)
            {
                Curve? curve = null;
                try { curve = seg.GetCurve(); } catch { }
                if (curve == null) continue;
                IList<XYZ> tess = curve.Tessellate();
                for (int i = 0; i < tess.Count - 1; i++)   // drop last point (shared with next segment)
                    pts.Add(tess[i]);
            }
            if (pts.Count >= 3) loops.Add(pts);
        }
        return loops;
    }

    private static bool PointInLoops(List<List<XYZ>> loops, XYZ point)
    {
        if (loops.Count == 0 || !PointInPolygon2D(loops[0], point))
            return false;
        for (int i = 1; i < loops.Count; i++)   // inside a hole (interior column) ⇒ not in the space
            if (PointInPolygon2D(loops[i], point))
                return false;
        return true;
    }

    private static bool PointInPolygon2D(List<XYZ> polygon, XYZ point)
    {
        if (polygon == null || polygon.Count < 3) return false;

        double px = point.X, py = point.Y;
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].X, yi = polygon[i].Y;
            double xj = polygon[j].X, yj = polygon[j].Y;
            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                inside = !inside;
        }
        return inside;
    }
}
