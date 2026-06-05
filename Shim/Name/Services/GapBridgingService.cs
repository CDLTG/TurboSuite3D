#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Detects wall endpoint gaps and bridges them with virtual wall segments.
/// Three passes: (0) door/window opening sealing — projects a 12-ft wall segment along every
/// parallel wall face near each opening position (no endpoint matching needed),
/// (1) collinear bridging for remaining gaps, (2) proximity bridging for corners.
/// </summary>
public static class GapBridgingService
{
    private const double ConnectionTolerance = 0.01;       // ft — endpoints closer than this are already connected
    private const double ProximityBridgeDistance = 1.0;      // 12 inches — bridge unconditionally (handles corners + thick walls)
    private const double MaxCollinearBridgeDistance = 15.0; // ft — max gap for collinear bridging
    private const double AngleTolerance = 5.0 * Math.PI / 180.0;
    private const double PerpendicularTolerance = 0.5;     // ft
    private const double DoorWallSearchRadius = 3.0;        // ft — max distance from door position to wall segment
    private const double DoorFacePerpTolerance = 1.5;       // ft — max perp distance to include parallel wall face
    private const double MinWallSegmentLength = 2.0;        // ft — skip jamb returns when finding wall direction
    private const double DoorBridgeMaxExtent = 6.0;         // ft — max bridge extent in each direction
    private const double DoorBridgeDefaultExtent = 1.0;     // ft — minimal overlap when no opening edge found
    private const double DoorBridgeMargin = 0.5;            // ft — margin past opening edge endpoints
    private const double DoorEdgeMinProj = 0.5;             // ft — skip endpoints too close to door position

    /// <summary>Diagnostic info from the last BridgeGaps call.</summary>
    public static string LastBridgeInfo { get; private set; }

    public static List<CadWallSegment> BridgeGaps(
        List<CadWallSegment> wallSegments,
        List<XYZ> doorPositions, List<XYZ> windowPositions)
    {
        var result = new List<CadWallSegment>(wallSegments);

        // Build endpoint list with parent segment direction
        var endpoints = new List<EndpointInfo>();
        for (int i = 0; i < wallSegments.Count; i++)
        {
            var seg = wallSegments[i];
            var diff = seg.EndPoint - seg.StartPoint;
            double len = diff.GetLength();
            var dir = len > 0.001 ? diff.Normalize() : new XYZ(1, 0, 0);
            endpoints.Add(new EndpointInfo(seg.StartPoint, dir, i));
            endpoints.Add(new EndpointInfo(seg.EndPoint, dir, i));
        }

        // Find unconnected endpoints (no other endpoint within tolerance)
        var unconnected = new List<EndpointInfo>();
        foreach (var ep in endpoints)
        {
            bool connected = endpoints.Any(other =>
                other.SegmentIndex != ep.SegmentIndex &&
                ep.Point.DistanceTo(other.Point) < ConnectionTolerance);
            if (!connected)
                unconnected.Add(ep);
        }

        var bridged = new HashSet<int>();
        int doorBridges = 0, collinearBridges = 0, proximityBridges = 0;

        // Pass 0: Seal door/window openings by projecting bridge segments along the
        // nearest wall line. Finds wall direction from nearest LONG segment (skips
        // short jamb returns), then sizes each bridge to the actual opening width
        // by scanning for the nearest wall-line endpoints on each side.
        var openingPositions = new List<XYZ>();
        if (doorPositions != null) openingPositions.AddRange(doorPositions);
        if (windowPositions != null) openingPositions.AddRange(windowPositions);

        foreach (var pos in openingPositions)
        {
            // Find reference direction from nearest LONG wall segment (skip jamb returns)
            CadWallSegment refSeg = null;
            double refDist = DoorWallSearchRadius;
            foreach (var seg in wallSegments)
            {
                if (seg.IsVirtual) continue;
                if ((seg.EndPoint - seg.StartPoint).GetLength() < MinWallSegmentLength) continue;
                double d = PointToSegmentDistance(pos, seg);
                if (d < refDist) { refDist = d; refSeg = seg; }
            }
            if (refSeg == null) continue;

            var refDir = (refSeg.EndPoint - refSeg.StartPoint).Normalize();

            // Create bridges on all parallel wall faces near this opening
            foreach (var seg in wallSegments)
            {
                if (seg.IsVirtual) continue;
                if ((seg.EndPoint - seg.StartPoint).GetLength() < MinWallSegmentLength) continue;
                double d = PointToSegmentDistance(pos, seg);
                if (d > DoorWallSearchRadius) continue;

                var segDir = (seg.EndPoint - seg.StartPoint).Normalize();

                double angle = AngleBetween(refDir, segDir);
                if (Math.Min(angle, Math.PI - angle) > AngleTolerance) continue;

                double perpDist = PerpendicularDistance(pos, seg.StartPoint, segDir);
                if (perpDist > DoorFacePerpTolerance) continue;

                // Determine bridge extent by scanning for nearest wall-line
                // endpoints on each side of the door position. This sizes the
                // bridge to the actual opening width instead of a fixed length.
                double plusExtent = DoorBridgeDefaultExtent;
                double minusExtent = DoorBridgeDefaultExtent;

                foreach (var ep in endpoints)
                {
                    double epPerp = PerpendicularDistance(ep.Point, seg.StartPoint, segDir);
                    if (epPerp > 0.5) continue; // must be on same wall line

                    double proj = (ep.Point - pos).DotProduct(segDir);
                    if (proj > DoorEdgeMinProj && proj < DoorBridgeMaxExtent)
                    {
                        if (proj + DoorBridgeMargin < plusExtent || plusExtent <= DoorBridgeDefaultExtent)
                            plusExtent = proj + DoorBridgeMargin;
                    }
                    else if (proj < -DoorEdgeMinProj && -proj < DoorBridgeMaxExtent)
                    {
                        double dist = -proj;
                        if (dist + DoorBridgeMargin < minusExtent || minusExtent <= DoorBridgeDefaultExtent)
                            minusExtent = dist + DoorBridgeMargin;
                    }
                }

                double t = (pos - seg.StartPoint).DotProduct(segDir);
                var projected = seg.StartPoint + segDir * t;
                var bridgeStart = projected - segDir * minusExtent;
                var bridgeEnd = projected + segDir * plusExtent;

                result.Add(new CadWallSegment(bridgeStart, bridgeEnd, IsVirtual: true));
                doorBridges++;
            }
        }

        // Pass 1: Collinear bridging — bridges across gaps where walls continue in
        // the same direction. Runs before proximity to avoid proximity greedily pairing
        // inner↔outer wall endpoints on the same side of a gap.
        for (int i = 0; i < unconnected.Count; i++)
        {
            if (bridged.Contains(i)) continue;
            var ep1 = unconnected[i];

            int bestIdx = -1;
            double bestDist = MaxCollinearBridgeDistance;

            for (int j = i + 1; j < unconnected.Count; j++)
            {
                if (bridged.Contains(j)) continue;
                if (unconnected[j].SegmentIndex == ep1.SegmentIndex) continue;

                double dist = ep1.Point.DistanceTo(unconnected[j].Point);
                if (dist > MaxCollinearBridgeDistance || dist < 0.001) continue;

                if (IsCollinear(ep1, unconnected[j], wallSegments) && dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = j;
                }
            }

            if (bestIdx >= 0)
            {
                result.Add(new CadWallSegment(ep1.Point, unconnected[bestIdx].Point, IsVirtual: true));
                bridged.Add(i);
                bridged.Add(bestIdx);
                collinearBridges++;
            }
        }

        // Pass 2: Proximity bridging — nearest neighbor within 12 inches, no angle check.
        // Handles corner gaps where perpendicular walls almost meet.
        for (int i = 0; i < unconnected.Count; i++)
        {
            if (bridged.Contains(i)) continue;
            var ep1 = unconnected[i];

            int bestIdx = -1;
            double bestDist = ProximityBridgeDistance;

            for (int j = i + 1; j < unconnected.Count; j++)
            {
                if (bridged.Contains(j)) continue;
                if (unconnected[j].SegmentIndex == ep1.SegmentIndex) continue;

                double dist = ep1.Point.DistanceTo(unconnected[j].Point);
                if (dist < bestDist && dist > 0.001)
                {
                    bestDist = dist;
                    bestIdx = j;
                }
            }

            if (bestIdx >= 0)
            {
                result.Add(new CadWallSegment(ep1.Point, unconnected[bestIdx].Point, IsVirtual: true));
                bridged.Add(i);
                bridged.Add(bestIdx);
                proximityBridges++;
            }
        }

        int remaining = unconnected.Count - bridged.Count;
        LastBridgeInfo = $"Bridges: {doorBridges} door, {collinearBridges} collinear, {proximityBridges} proximity ({remaining} unbridged of {unconnected.Count} unconnected)";
        return result;
    }

    private static bool IsCollinear(EndpointInfo ep1, EndpointInfo ep2, List<CadWallSegment> allSegments)
    {
        double angle = AngleBetween(ep1.Direction, ep2.Direction);
        double minAngle = Math.Min(angle, Math.PI - angle);
        if (minAngle > AngleTolerance) return false;

        double perpDist1 = PerpendicularDistance(ep2.Point, ep1.Point, ep1.Direction);
        double perpDist2 = PerpendicularDistance(ep1.Point, ep2.Point, ep2.Direction);
        if (Math.Min(perpDist1, perpDist2) > PerpendicularTolerance) return false;

        if (AnyCrossingSegment(ep1.Point, ep2.Point, allSegments, ep1.SegmentIndex, ep2.SegmentIndex))
            return false;

        return true;
    }

    private static double AngleBetween(XYZ a, XYZ b)
    {
        double dot = a.X * b.X + a.Y * b.Y;
        dot = Math.Max(-1, Math.Min(1, dot));
        return Math.Acos(dot);
    }

    private static double PerpendicularDistance(XYZ point, XYZ linePoint, XYZ lineDir)
    {
        var diff = point - linePoint;
        double dot = diff.X * lineDir.X + diff.Y * lineDir.Y;
        var projection = new XYZ(linePoint.X + dot * lineDir.X, linePoint.Y + dot * lineDir.Y, 0);
        return point.DistanceTo(projection);
    }

    private static bool AnyCrossingSegment(XYZ a, XYZ b, List<CadWallSegment> segments, int skip1, int skip2)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            if (i == skip1 || i == skip2) continue;
            if (SegmentsIntersect2D(a, b, segments[i].StartPoint, segments[i].EndPoint))
                return true;
        }
        return false;
    }

    private static bool SegmentsIntersect2D(XYZ p1, XYZ p2, XYZ p3, XYZ p4)
    {
        double d1 = Cross2D(p3, p4, p1);
        double d2 = Cross2D(p3, p4, p2);
        double d3 = Cross2D(p1, p2, p3);
        double d4 = Cross2D(p1, p2, p4);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        return false;
    }

    private static double Cross2D(XYZ a, XYZ b, XYZ c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static double PointToSegmentDistance(XYZ point, CadWallSegment seg)
    {
        var segDir = seg.EndPoint - seg.StartPoint;
        double segLen = segDir.GetLength();
        if (segLen < 0.001) return point.DistanceTo(seg.StartPoint);
        var segNorm = segDir / segLen;
        double t = (point - seg.StartPoint).DotProduct(segNorm);
        t = Math.Max(0, Math.Min(segLen, t));
        var closest = seg.StartPoint + segNorm * t;
        return point.DistanceTo(closest);
    }

    private record EndpointInfo(XYZ Point, XYZ Direction, int SegmentIndex);
}
