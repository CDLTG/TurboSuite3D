using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Mask.Services;

/// <summary>
/// Computes a bounding box over a set of elements and returns an outer CurveLoop, offset outward
/// by a fixed margin so the masking region extends past the selected content. The box is oriented
/// to the selection's own dominant rotation (so a diagonal driver stack / tape run gets a snug
/// rectangle that runs with it, not a wasteful axis-aligned box), falling back to axis-aligned when
/// the selection has no consistent rotation. Because the region co-rotates with the model, this is
/// crop-invariant — it hugs the selection at any view crop rotation. Returns null if no element has
/// a usable bounding box.
/// </summary>
internal static class SelectionBoundsService
{
    /// <summary>Hardcoded outward offset (feet). 3.5 inches / 12.0.</summary>
    public const double OutwardOffsetFeet = 3.5 / 12.0;

    public static CurveLoop? BuildOuterLoop(IEnumerable<Element> elements, View view)
    {
        var elementList = elements as IList<Element> ?? elements.ToList();

        double theta = DetermineOrientation(elementList);
        // Rotate content into the oriented frame to measure min/max, then rotate the finished
        // rectangle back into model space. Identity when theta == 0 (axis-aligned fallback).
        Transform toLocal = Transform.CreateRotation(XYZ.BasisZ, -theta);
        Transform toModel = Transform.CreateRotation(XYZ.BasisZ, theta);

        bool any = false;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        double z = view.GenLevel?.Elevation ?? 0.0;

        foreach (var element in elementList)
        {
            var bbox = element.get_BoundingBox(view);
            if (bbox == null) continue;

            any = true;
            // Project all four in-plane corners of the (world-aligned) bbox into the oriented
            // frame — a corner sweep, not just Min/Max, so a rotated box measures correctly.
            foreach (var corner in PlanCorners(bbox))
            {
                var local = toLocal.OfPoint(corner);
                if (local.X < minX) minX = local.X;
                if (local.Y < minY) minY = local.Y;
                if (local.X > maxX) maxX = local.X;
                if (local.Y > maxY) maxY = local.Y;
            }
        }

        if (!any) return null;

        minX -= OutwardOffsetFeet;
        minY -= OutwardOffsetFeet;
        maxX += OutwardOffsetFeet;
        maxY += OutwardOffsetFeet;

        var p0 = FlattenToLevel(toModel.OfPoint(new XYZ(minX, minY, 0)), z);
        var p1 = FlattenToLevel(toModel.OfPoint(new XYZ(maxX, minY, 0)), z);
        var p2 = FlattenToLevel(toModel.OfPoint(new XYZ(maxX, maxY, 0)), z);
        var p3 = FlattenToLevel(toModel.OfPoint(new XYZ(minX, maxY, 0)), z);

        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));

        if (!loop.HasPlane()) return null;
        if (IsClockwise(loop))
            loop.Flip();

        return loop;
    }

    /// <summary>
    /// The dominant in-plane rotation of the selected family instances, in radians, or 0 when there
    /// is no clear majority (mixed or unrotated selection → axis-aligned box, i.e. legacy behavior).
    /// Angles are folded into [0, 90°) because a rectangle's enclosing box is invariant under 90°
    /// turns and flips, so every element of a consistent stack lands in the same bucket.
    /// </summary>
    private static double DetermineOrientation(IEnumerable<Element> elements)
    {
        var buckets = new Dictionary<int, int>();
        int total = 0;

        foreach (var element in elements)
        {
            if (element is not FamilyInstance fi) continue;

            double a = GeometryHelper.GetTransformAngle(fi.GetTotalTransform());
            a %= Math.PI / 2.0;
            if (a < 0) a += Math.PI / 2.0;

            int deg = (int)Math.Round(a * 180.0 / Math.PI) % 90;
            buckets.TryGetValue(deg, out int count);
            buckets[deg] = count + 1;
            total++;
        }

        if (total == 0) return 0.0;

        var best = buckets.OrderByDescending(kv => kv.Value).First();
        // Require a strict majority so a mixed bag stays axis-aligned.
        if (best.Value * 2 <= total) return 0.0;

        return best.Key * Math.PI / 180.0;
    }

    private static IEnumerable<XYZ> PlanCorners(BoundingBoxXYZ bbox)
    {
        yield return new XYZ(bbox.Min.X, bbox.Min.Y, 0);
        yield return new XYZ(bbox.Max.X, bbox.Min.Y, 0);
        yield return new XYZ(bbox.Max.X, bbox.Max.Y, 0);
        yield return new XYZ(bbox.Min.X, bbox.Max.Y, 0);
    }

    private static XYZ FlattenToLevel(XYZ p, double z) => new XYZ(p.X, p.Y, z);

    private static bool IsClockwise(CurveLoop loop)
    {
        double sum = 0;
        foreach (Curve c in loop)
        {
            var s = c.GetEndPoint(0);
            var e = c.GetEndPoint(1);
            sum += (e.X - s.X) * (e.Y + s.Y);
        }
        return sum > 0;
    }
}
