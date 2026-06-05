#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Provides room name lookup from "Room Region" FilledRegions as a fallback
/// when no Revit Room elements exist (2D drafting workflows).
/// Also supports writing room name overrides back to region Comments.
/// </summary>
public class RegionRoomLookupService
{
    private const string RoomRegionTypeName = "Room Region";

    private readonly List<RegionEntry> _regions;

    /// <summary>
    /// Creates a region lookup from a specific view (for TurboName — view-scoped).
    /// </summary>
    public RegionRoomLookupService(Document doc, View view)
        : this(doc, new FilteredElementCollector(doc, view.Id)) { }

    /// <summary>
    /// Creates a region lookup from all "Room Region" FilledRegions in the entire document
    /// (for TurboZones/TurboNumber — project-wide).
    /// </summary>
    public RegionRoomLookupService(Document doc)
        : this(doc, new FilteredElementCollector(doc)) { }

    private RegionRoomLookupService(Document doc, FilteredElementCollector collector)
    {
        _regions = new List<RegionEntry>();

        var allRegions = collector
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .ToList();

        foreach (var region in allRegions)
        {
            if (region.IsMasking) continue;

            var typeId = region.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;
            var regionType = doc.GetElement(typeId);
            if (regionType == null || regionType.Name != RoomRegionTypeName) continue;

            string comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? "";
            if (string.IsNullOrWhiteSpace(comments)) continue;

            var boundaries = region.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0) continue;

            var loopPoints = new List<List<XYZ>>();
            foreach (var loop in boundaries)
            {
                var points = new List<XYZ>();
                foreach (var curve in loop)
                {
                    var tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++)
                        points.Add(tessellated[i]);
                }
                loopPoints.Add(points);
            }

            _regions.Add(new RegionEntry(region.Id, comments, loopPoints));
        }
    }

    /// <summary>
    /// Returns the room name (Comments) of the "Room Region" containing the given point,
    /// or null if no region contains it.
    /// </summary>
    public string FindRoomName(XYZ point)
    {
        foreach (var entry in _regions)
        {
            if (IsPointInZone(entry.Loops, point))
                return entry.Comments;
        }
        return null;
    }

    /// <summary>
    /// Returns the ElementId of the "Room Region" containing the given point,
    /// or InvalidElementId if no region contains it.
    /// </summary>
    public ElementId FindRegionId(XYZ point)
    {
        foreach (var entry in _regions)
        {
            if (IsPointInZone(entry.Loops, point))
                return entry.RegionId;
        }
        return ElementId.InvalidElementId;
    }

    /// <summary>
    /// Writes a room name override to the Comments parameter of the specified region.
    /// Must be called inside an active Transaction.
    /// </summary>
    public static void WriteRoomNameToRegion(Document doc, ElementId regionId, string roomName)
    {
        if (regionId == ElementId.InvalidElementId) return;
        var element = doc.GetElement(regionId);
        element?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            ?.Set(roomName);
    }

    private static bool IsPointInZone(List<List<XYZ>> loops, XYZ point)
    {
        bool hit = IsPointInPolygon2D(loops[0], point);

        // Boundary-touching fallback: if the point sits exactly on a region edge
        // (e.g., keypads placed on walls that coincide with region boundaries),
        // nudge it slightly inward and re-test. Try centroid direction plus
        // cardinal directions to handle corners where two edges meet.
        if (!hit)
        {
            const double nudge = 0.03125; // 3/8 inch
            XYZ centroid = ComputeCentroid(loops[0]);
            double cdx = centroid.X - point.X;
            double cdy = centroid.Y - point.Y;
            double cdist = System.Math.Sqrt(cdx * cdx + cdy * cdy);

            var directions = new List<(double dx, double dy)>();
            if (cdist > 1e-9)
                directions.Add((cdx / cdist, cdy / cdist));
            directions.Add((1, 0));
            directions.Add((-1, 0));
            directions.Add((0, 1));
            directions.Add((0, -1));

            foreach (var (dx, dy) in directions)
            {
                var nudged = new XYZ(
                    point.X + dx * nudge,
                    point.Y + dy * nudge,
                    point.Z);
                if (IsPointInPolygon2D(loops[0], nudged))
                {
                    hit = true;
                    break;
                }
            }
        }

        if (hit && loops.Count > 1)
        {
            for (int i = 1; i < loops.Count; i++)
            {
                if (IsPointInPolygon2D(loops[i], point))
                    return false;
            }
        }
        return hit;
    }

    private static XYZ ComputeCentroid(List<XYZ> polygon)
    {
        double cx = 0, cy = 0;
        foreach (var pt in polygon) { cx += pt.X; cy += pt.Y; }
        int n = polygon.Count;
        return new XYZ(cx / n, cy / n, 0);
    }

    private static bool IsPointInPolygon2D(List<XYZ> polygon, XYZ point)
    {
        if (polygon == null || polygon.Count < 3) return false;

        double px = point.X;
        double py = point.Y;
        bool inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].X, yi = polygon[i].Y;
            double xj = polygon[j].X, yj = polygon[j].Y;

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private record RegionEntry(ElementId RegionId, string Comments, List<List<XYZ>> Loops);
}
