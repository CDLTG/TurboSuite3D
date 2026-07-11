#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Name.Services;

/// <summary>
/// Creates a single FilledRegion from a boundary polygon.
/// </summary>
public static class RegionCreationService
{
    /// <summary>
    /// Creates a FilledRegion from a boundary polygon. Must be called inside an active Transaction.
    /// Returns the created FilledRegion's ElementId, or InvalidElementId on failure.
    /// </summary>
    public static ElementId CreateRegion(Document doc, View view,
        List<XYZ> boundary, ElementId regionTypeId) =>
        CreateRegion(doc, view, boundary, regionTypeId, out _);

    /// <summary>
    /// As above, but reports the rejection reason (null on success) for diagnostics.
    /// </summary>
    public static ElementId CreateRegion(Document doc, View view,
        List<XYZ> boundary, ElementId regionTypeId, out string reason)
    {
        reason = null;
        if (boundary == null || boundary.Count < 3)
        {
            reason = $"boundary too small ({boundary?.Count ?? 0} pts)";
            return ElementId.InvalidElementId;
        }

        try
        {
            var loop = new CurveLoop();
            int edges = 0;
            for (int i = 0; i < boundary.Count; i++)
            {
                var start = boundary[i];
                var end = boundary[(i + 1) % boundary.Count];

                // Skip degenerate edges
                if (start.DistanceTo(end) < 0.001)
                    continue;

                loop.Append(Line.CreateBound(start, end));
                edges++;
            }

            // Ensure counter-clockwise orientation (required for outer boundary)
            if (loop.IsOpen()) { reason = $"open loop ({edges} edges)"; return ElementId.InvalidElementId; }
            if (!loop.HasPlane()) { reason = "no plane"; return ElementId.InvalidElementId; }

            if (IsClockwise(boundary))
                loop.Flip();

            var region = FilledRegion.Create(doc, regionTypeId, view.Id,
                new List<CurveLoop> { loop });

            return region.Id;
        }
        catch (Exception ex)
        {
            reason = $"exception: {ex.Message}";
            return ElementId.InvalidElementId;
        }
    }

    private static bool IsClockwise(List<XYZ> polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            sum += (next.X - current.X) * (next.Y + current.Y);
        }
        return sum > 0;
    }
}
