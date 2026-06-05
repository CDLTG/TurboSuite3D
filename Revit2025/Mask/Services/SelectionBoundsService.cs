using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Mask.Services;

/// <summary>
/// Computes a view-aligned bounding box union over a set of elements and returns an outer
/// CurveLoop, offset outward by a fixed margin so the masking region extends past the selected
/// content. Returns null if no element has a usable bounding box.
/// </summary>
internal static class SelectionBoundsService
{
    /// <summary>Hardcoded outward offset (feet). 3.5 inches / 12.0.</summary>
    public const double OutwardOffsetFeet = 3.5 / 12.0;

    public static CurveLoop? BuildOuterLoop(IEnumerable<Element> elements, View view)
    {
        bool any = false;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        double z = view.GenLevel?.Elevation ?? 0.0;

        foreach (var element in elements)
        {
            var bbox = element.get_BoundingBox(view);
            if (bbox == null) continue;

            any = true;
            if (bbox.Min.X < minX) minX = bbox.Min.X;
            if (bbox.Min.Y < minY) minY = bbox.Min.Y;
            if (bbox.Max.X > maxX) maxX = bbox.Max.X;
            if (bbox.Max.Y > maxY) maxY = bbox.Max.Y;
        }

        if (!any) return null;

        minX -= OutwardOffsetFeet;
        minY -= OutwardOffsetFeet;
        maxX += OutwardOffsetFeet;
        maxY += OutwardOffsetFeet;

        var p0 = new XYZ(minX, minY, z);
        var p1 = new XYZ(maxX, minY, z);
        var p2 = new XYZ(maxX, maxY, z);
        var p3 = new XYZ(minX, maxY, z);

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
