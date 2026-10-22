using System;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;
using TurboSuite.Tag.Constants;
using TurboSuite.Tag.Models;

namespace TurboSuite.Tag.Services;

internal static class TagPlacementService
{
    public static XYZ TransformToGlobal(FamilyInstance fixture, XYZ localOffset)
    {
        double angle = GeometryHelper.GetTransformAngle(fixture.GetTransform());

        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double gx = localOffset.X * cos - localOffset.Y * sin;
        double gy = localOffset.X * sin + localOffset.Y * cos;

        return new XYZ(gx, gy, 0);
    }

    public static XYZ CalculateOffset(TagDirection direction, double symbolLength, double symbolWidth, double tagWidth, TagGeometry g)
    {
        return direction switch
        {
            TagDirection.Up => new XYZ(0, (symbolLength / 2.0) + g.VerticalOffsetFeet, 0),
            TagDirection.Down => new XYZ(0, -((symbolLength / 2.0) + g.VerticalOffsetFeet), 0),
            TagDirection.Right => new XYZ((symbolWidth / 2.0) + (tagWidth / 2.0) + g.OffsetMarginRightFeet, 0, 0),
            TagDirection.Left => new XYZ(-((symbolWidth / 2.0) + (tagWidth / 2.0) + g.OffsetMarginLeftFeet), 0, 0),
            _ => XYZ.Zero
        };
    }

    public static bool IsLineReversed(FamilyInstance fixture)
    {
        if (fixture.Location is not LocationCurve locCurve)
            return false;

        Curve curve = locCurve.Curve;
        XYZ startPoint = curve.GetEndPoint(0);
        XYZ endPoint = curve.GetEndPoint(1);
        XYZ direction = (endPoint - startPoint).Normalize();

        return direction.X < -0.001 || (Math.Abs(direction.X) < 0.001 && direction.Y < -0.001);
    }

    public static XYZ CalculateLinearOffset(TagDirection direction, bool isReversed, TagGeometry g)
    {
        double offset = isReversed ? -g.LinearOffsetFeet : g.LinearOffsetFeet;

        return direction switch
        {
            TagDirection.Up => new XYZ(0, offset, 0),
            TagDirection.Down => new XYZ(0, -offset, 0),
            _ => XYZ.Zero
        };
    }

    public static double EstimateTagWidth(IndependentTag tag, Document doc, ElementId viewId, TagGeometry g)
    {
        try
        {
            string? tagText = tag.TagText;

            if (string.IsNullOrEmpty(tagText) || tagText.Length <= TagConstants.ShortTextThreshold)
            {
                return g.DefaultTagWidthShort;
            }

            if (tagText.Length == TagConstants.MediumTextThreshold)
            {
                return g.DefaultTagWidthMedium;
            }

            View? view = viewId != ElementId.InvalidElementId ? doc.GetElement(viewId) as View : null;
            BoundingBoxXYZ? bbox = tag.get_BoundingBox(view);

            if (bbox != null)
            {
                return bbox.Max.X - bbox.Min.X;
            }

            return g.DefaultTagWidthMedium;
        }
        catch
        {
            return g.DefaultTagWidthShort;
        }
    }
}
