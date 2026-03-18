using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;
using TurboSuite.Wire.Constants;

namespace TurboSuite.Wire.Helpers;

internal static class ArcCalculator
{
    // Ratio threshold: min(dx,dy)/max(dx,dy) above this → "squared" (corner arc),
    // below → "elongated" (S-spline). 0.6 ≈ 31° from axis.
    private const double SquaredRatioThreshold = 0.6;

    /// <summary>
    /// Determines if two points are off-axis (not aligned horizontally or vertically).
    /// Returns true when both X and Y deltas exceed the threshold.
    /// </summary>
    public static bool IsOffAxis(XYZ p1, XYZ p2, double threshold = 0.5)
    {
        double dx = Math.Abs(p1.X - p2.X);
        double dy = Math.Abs(p1.Y - p2.Y);
        return dx > threshold && dy > threshold;
    }

    /// <summary>
    /// Returns true when the two points are roughly diagonal (dx ≈ dy).
    /// </summary>
    public static bool IsSquared(XYZ p1, XYZ p2)
    {
        double dx = Math.Abs(p1.X - p2.X);
        double dy = Math.Abs(p1.Y - p2.Y);
        double max = Math.Max(dx, dy);
        if (max < 1e-9) return false;
        return Math.Min(dx, dy) / max >= SquaredRatioThreshold;
    }

    // How far the two middle vertices pull back from the corner (0–1).
    // 1/3 keeps the curve close to the corner without overshooting.
    private const double CornerPullBack = 11.0 / 24.0;

    /// <summary>
    /// Creates a 4-point smoothed corner arc for near-diagonal fixtures.
    /// Two middle vertices ease into and out of the bounding-box corner,
    /// pulled back along each leg by CornerPullBack.
    /// </summary>
    public static IList<XYZ> CalculateCornerArcPoints(XYZ p1, XYZ p2, int arcDirection)
    {
        double midZ = (p1.Z + p2.Z) * 0.5;

        // Determine which bounding-box corner the perpendicular direction points to.
        XYZ p1Flat = new XYZ(p1.X, p1.Y, 0);
        XYZ p2Flat = new XYZ(p2.X, p2.Y, 0);
        XYZ mid = (p1Flat + p2Flat) * 0.5;
        XYZ chordDir = (p2Flat - p1Flat).Normalize();
        XYZ perpDir = XYZ.BasisZ.CrossProduct(chordDir).Normalize();

        XYZ cornerA = new XYZ(p1.X, p2.Y, 0);
        double dot = (cornerA - mid).DotProduct(perpDir);
        bool useCornerA = (dot >= 0) == (arcDirection >= 0);

        XYZ corner = useCornerA
            ? new XYZ(p1.X, p2.Y, midZ)
            : new XYZ(p2.X, p1.Y, midZ);

        // Pull back from corner toward p1 and p2
        double t = CornerPullBack;
        XYZ v1 = new XYZ(
            corner.X + (p1.X - corner.X) * t,
            corner.Y + (p1.Y - corner.Y) * t,
            midZ);
        XYZ v2 = new XYZ(
            corner.X + (p2.X - corner.X) * t,
            corner.Y + (p2.Y - corner.Y) * t,
            midZ);

        return new List<XYZ> { p1, v1, v2, p2 };
    }

    /// <summary>
    /// Creates an S-shaped spline between two off-axis fixtures.
    /// The S transitions along the longer axis, with the two middle vertices
    /// offset along the shorter axis. This keeps the spline compact.
    /// </summary>
    public static IList<XYZ> CalculateSSplinePoints(XYZ p1, XYZ p2)
    {
        double dx = Math.Abs(p2.X - p1.X);
        double dy = Math.Abs(p2.Y - p1.Y);
        double midZ = (p1.Z + p2.Z) * 0.5;

        if (dx >= dy)
        {
            // Longer axis is X — step along X to midpoint, short jog in Y
            double midX = (p1.X + p2.X) * 0.5;
            return new List<XYZ>
            {
                p1,
                new XYZ(midX, p1.Y, midZ),
                new XYZ(midX, p2.Y, midZ),
                p2
            };
        }
        else
        {
            // Longer axis is Y — step along Y to midpoint, short jog in X
            double midY = (p1.Y + p2.Y) * 0.5;
            return new List<XYZ>
            {
                p1,
                new XYZ(p1.X, midY, midZ),
                new XYZ(p2.X, midY, midZ),
                p2
            };
        }
    }

    public static IList<XYZ> CalculateArcWirePoints(XYZ p1, XYZ p2, double angleDeg, int arcDirection = 1)
    {
        XYZ p1Flat = new XYZ(p1.X, p1.Y, 0);
        XYZ p2Flat = new XYZ(p2.X, p2.Y, 0);

        XYZ midpoint = (p1Flat + p2Flat) * 0.5;
        XYZ chordDirection = (p2Flat - p1Flat).Normalize();
        XYZ perpendicular = XYZ.BasisZ.CrossProduct(chordDirection).Normalize();

        double angleRad = angleDeg * Math.PI / 180.0;
        double chordLength = p1Flat.DistanceTo(p2Flat);
        double arcHeight = (chordLength / 2.0) * Math.Tan(angleRad / 2.0);

        XYZ vertexXY = midpoint + perpendicular * arcHeight * arcDirection;
        double vertexZ = (p1.Z + p2.Z) * 0.5;

        return new List<XYZ>
        {
            p1,
            new XYZ(vertexXY.X, vertexXY.Y, vertexZ),
            p2
        };
    }

    public static Dictionary<ElementId, IndependentTag> BuildTagLookup(Document doc)
    {
        var lookup = new Dictionary<ElementId, IndependentTag>();
        var tagCollector = new FilteredElementCollector(doc)
            .OfClass(typeof(IndependentTag));

        foreach (IndependentTag tag in tagCollector)
        {
            foreach (Element taggedElement in tag.GetTaggedLocalElements())
            {
                if (!lookup.ContainsKey(taggedElement.Id))
                    lookup[taggedElement.Id] = tag;
            }
        }

        return lookup;
    }

    private const double PerpThreshold = 0.3;

    public static int? GetArcDirectionFromTags(Document doc, FamilyInstance fixture1, FamilyInstance fixture2, XYZ p1, XYZ p2,
        Dictionary<ElementId, IndependentTag>? tagLookup = null)
    {
        XYZ p1Flat = new XYZ(p1.X, p1.Y, 0);
        XYZ p2Flat = new XYZ(p2.X, p2.Y, 0);
        XYZ chordDir = (p2Flat - p1Flat).Normalize();
        XYZ perpDir = XYZ.BasisZ.CrossProduct(chordDir).Normalize();

        XYZ? tagOffset1 = GetTagOffsetDirection(doc, fixture1, tagLookup);
        XYZ? tagOffset2 = GetTagOffsetDirection(doc, fixture2, tagLookup);

        double? dot1 = tagOffset1 != null ? tagOffset1.DotProduct(perpDir) : null;
        double? dot2 = tagOffset2 != null ? tagOffset2.DotProduct(perpDir) : null;

        bool vote1 = dot1.HasValue && Math.Abs(dot1.Value) >= PerpThreshold;
        bool vote2 = dot2.HasValue && Math.Abs(dot2.Value) >= PerpThreshold;

        if (vote1 && vote2)
        {
            // Both tags have strong perpendicular component — use if they agree
            if ((dot1!.Value > 0) == (dot2!.Value > 0))
                return dot1.Value >= 0 ? 1 : -1;
            // Tags disagree — can't determine
            return null;
        }

        if (vote1)
            return dot1!.Value >= 0 ? 1 : -1;

        if (vote2)
            return dot2!.Value >= 0 ? 1 : -1;

        // Tags are parallel to chord or absent — can't determine
        return null;
    }

    public static XYZ? GetTagOffsetDirection(Document doc, FamilyInstance fixture,
        Dictionary<ElementId, IndependentTag>? tagLookup = null)
    {
        ElementId fixtureId = fixture.Id;
        XYZ? fixtureLocation = GeometryHelper.GetFixtureLocation(fixture);
        if (fixtureLocation == null) return null;

        IndependentTag? tag = null;

        if (tagLookup != null)
        {
            tagLookup.TryGetValue(fixtureId, out tag);
        }
        else
        {
            var tagCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(IndependentTag));

            foreach (IndependentTag candidate in tagCollector)
            {
                if (candidate.GetTaggedLocalElements().Any(e => e.Id == fixtureId))
                {
                    tag = candidate;
                    break;
                }
            }
        }

        if (tag != null)
        {
            XYZ? tagHead = tag.TagHeadPosition;
            if (tagHead != null)
            {
                XYZ offset = tagHead - fixtureLocation;
                XYZ offsetFlat = new XYZ(offset.X, offset.Y, 0);
                if (offsetFlat.GetLength() > 0.001)
                {
                    return offsetFlat.Normalize();
                }
            }
        }

        return null;
    }
}
