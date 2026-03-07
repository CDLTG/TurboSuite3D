using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;
using TurboSuite.Wire.Constants;

namespace TurboSuite.Wire.Helpers;

internal static class ArcCalculator
{
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
