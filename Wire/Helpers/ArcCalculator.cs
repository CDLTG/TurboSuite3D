using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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

    public static int GetArcDirectionFromTags(Document doc, FamilyInstance fixture1, FamilyInstance fixture2, XYZ p1, XYZ p2)
    {
        XYZ p1Flat = new XYZ(p1.X, p1.Y, 0);
        XYZ p2Flat = new XYZ(p2.X, p2.Y, 0);
        XYZ chordDir = (p2Flat - p1Flat).Normalize();
        XYZ perpDir = XYZ.BasisZ.CrossProduct(chordDir).Normalize();

        XYZ? tagOffset = GetTagOffsetDirection(doc, fixture1);
        if (tagOffset == null)
        {
            tagOffset = GetTagOffsetDirection(doc, fixture2);
        }

        if (tagOffset != null)
        {
            double dot = tagOffset.DotProduct(perpDir);
            return dot >= 0 ? 1 : -1;
        }

        return -1;
    }

    public static XYZ? GetTagOffsetDirection(Document doc, FamilyInstance fixture)
    {
        ElementId fixtureId = fixture.Id;
        XYZ fixtureLocation = ((LocationPoint)fixture.Location).Point;

        FilteredElementCollector tagCollector = new FilteredElementCollector(doc)
            .OfClass(typeof(IndependentTag));

        foreach (IndependentTag tag in tagCollector)
        {
            ICollection<Element> taggedElements = tag.GetTaggedLocalElements();
            bool tagsThisFixture = taggedElements.Any(e => e.Id == fixtureId);

            if (tagsThisFixture)
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
        }

        return null;
    }
}
