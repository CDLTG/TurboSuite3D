using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Base class with shared placement calculation logic.
/// </summary>
internal abstract class PlacementCalculatorBase : IPlacementCalculator
{
    // Input data
    public XYZ FixturePoint { get; }
    public double Rotation { get; }
    public bool RotatesWithComponent { get; }

    // Calculated positions
    public XYZ NewTagPosition { get; protected set; } = XYZ.Zero;
    public XYZ Vertex2 { get; protected set; } = XYZ.Zero;
    public XYZ Vertex3 { get; protected set; } = XYZ.Zero;
    public bool IsFlipped { get; protected set; }

    // Internal state
    protected readonly XYZ TagPoint;
    protected readonly XYZ FixtureLocal;
    protected readonly XYZ TagLocal;
    protected readonly Transform GlobalToLocal;
    protected readonly Transform LocalToGlobal;
    protected readonly PlacementCondition Condition;
    protected readonly double SymbolLength;
    protected readonly double SymbolWidth;

    protected PlacementCalculatorBase(Document doc, View view, FamilyInstance fixture, IndependentTag sourceTag)
    {
        FixturePoint = GeometryHelper.GetFixtureLocation(fixture)
            ?? throw new InvalidOperationException("Fixture has no valid location for placement calculation.");
        TagPoint = sourceTag.TagHeadPosition;

        RotatesWithComponent = DetermineRotationMode(sourceTag);
        Rotation = CalculateRotation(fixture, sourceTag, RotatesWithComponent);

        GlobalToLocal = Transform.CreateRotationAtPoint(XYZ.BasisZ, -Rotation, FixturePoint);
        LocalToGlobal = GlobalToLocal.Inverse;

        FixtureLocal = GlobalToLocal.OfPoint(FixturePoint);
        TagLocal = GlobalToLocal.OfPoint(TagPoint);

        Condition = DetermineCondition(FixtureLocal, TagLocal);
        (SymbolLength, SymbolWidth) = CalculateSymbolDimensions(view, fixture, sourceTag);
    }

    public abstract void CalculateFinalPositions(XYZ flipPoint);

    internal static bool DetermineRotationMode(IndependentTag tag)
    {
        var param = tag.LookupParameter("Orientation");
        if (param == null) return false;

        var value = param.AsValueString();
        return value == "Horizontal" || value == "Vertical";
    }

    protected static double CalculateRotation(FamilyInstance fixture, IndependentTag tag, bool rotatesWithComponent)
    {
        if (rotatesWithComponent)
            return GetFixtureRotation(fixture);

        return tag.LookupParameter("Angle")?.AsDouble() ?? 0.0;
    }

    protected static double GetFixtureRotation(FamilyInstance fixture)
    {
        using (var options = new Options { ComputeReferences = false })
        {
            var geometry = fixture.get_Geometry(options);
            if (geometry != null)
            {
                foreach (var obj in geometry)
                {
                    if (obj is GeometryInstance instance)
                    {
                        var xAxis = instance.Transform.BasisX;
                        var rotation = Math.Atan2(xAxis.Y, xAxis.X);
                        if (Math.Abs(rotation) > BubbleConstants.RotationEpsilon)
                            return rotation;
                        break;
                    }
                }
            }
        }

        return GeometryHelper.GetFixtureLocationRotation(fixture);
    }

    protected static PlacementCondition DetermineCondition(XYZ fixtureLocal, XYZ tagLocal)
    {
        var diffX = tagLocal.X - fixtureLocal.X;
        var diffY = tagLocal.Y - fixtureLocal.Y;

        if (Math.Abs(diffX) > Math.Abs(diffY))
            return diffX > 0 ? PlacementCondition.Right : PlacementCondition.Left;

        return diffY > 0 ? PlacementCondition.Up : PlacementCondition.Down;
    }

    internal static (double length, double width) CalculateSymbolDimensions(View view, FamilyInstance fixture, IndependentTag tag)
    {
        var (length, width) = GeometryHelper.GetSymbolExtents(fixture, view, BubbleConstants.DefaultSymbolSizeFt);

        width = Math.Max(width, BubbleConstants.MinSymbolWidthFt);

        var tagWidth = GetTagWidth(view, tag);
        width = Math.Max(width, tagWidth);

        return (length, width);
    }

    internal static double GetTagWidth(View view, IndependentTag tag)
    {
        var charCount = tag.TagText?.Length ?? 0;

        return charCount switch
        {
            1 => BubbleConstants.TagWidth1CharFt,
            2 => BubbleConstants.TagWidth2CharsFt,
            3 => BubbleConstants.TagWidth3CharsFt,
            _ => CalculateTagWidthFromBounds(view, tag)
        };
    }

    internal static double CalculateTagWidthFromBounds(View view, IndependentTag tag)
    {
        var bbox = tag.get_BoundingBox(view);
        return bbox != null ? Math.Abs(bbox.Max.X - bbox.Min.X) : 0;
    }
}
