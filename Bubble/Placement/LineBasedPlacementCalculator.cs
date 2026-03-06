using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Handles placement calculations for line-based fixtures (e.g., linear light fixtures).
/// These fixtures have a LocationCurve instead of LocationPoint.
/// Tag placement is relative to the fixture's electrical connector.
/// Only Up/Down conditions are used (perpendicular to the fixture line).
/// </summary>
internal class LineBasedPlacementCalculator : IPlacementCalculator
{
    public XYZ FixturePoint { get; }
    public double Rotation { get; }
    public bool RotatesWithComponent { get; }

    public XYZ NewTagPosition { get; private set; } = XYZ.Zero;
    public XYZ Vertex2 { get; private set; } = XYZ.Zero;
    public XYZ Vertex3 { get; private set; } = XYZ.Zero;
    public bool IsFlipped { get; private set; }

    /// <summary>
    /// True if line direction is between 91 and 270 degrees (X component is negative).
    /// Used to correct tag type selection.
    /// </summary>
    public bool IsLineDirectionReversed { get; }

    private readonly XYZ _lineDirection;
    private readonly XYZ _perpDirection;
    private readonly Transform _globalToLocal;
    private readonly Transform _localToGlobal;

    private readonly XYZ _connectorLocal;
    private PlacementCondition _condition;
    private readonly double _symbolLength;
    private readonly double _symbolWidth;

    public LineBasedPlacementCalculator(Document doc, View view, FamilyInstance fixture, IndependentTag sourceTag)
    {
        var connector = GeometryHelper.GetElectricalConnector(fixture);
        FixturePoint = connector?.Origin ?? GetCurveMidpoint(fixture);

        RotatesWithComponent = DetermineRotationMode(sourceTag);

        var locationCurve = (LocationCurve)fixture.Location;
        var curve = locationCurve.Curve;
        _lineDirection = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
        _lineDirection = new XYZ(_lineDirection.X, _lineDirection.Y, 0).Normalize();

        IsLineDirectionReversed = _lineDirection.X < -BubbleConstants.RotationEpsilon ||
            (Math.Abs(_lineDirection.X) <= BubbleConstants.RotationEpsilon && _lineDirection.Y < 0);

        _perpDirection = new XYZ(-_lineDirection.Y, _lineDirection.X, 0);

        Rotation = Math.Atan2(_lineDirection.Y, _lineDirection.X);

        _globalToLocal = Transform.CreateRotationAtPoint(XYZ.BasisZ, -Rotation, FixturePoint);
        _localToGlobal = _globalToLocal.Inverse;

        _connectorLocal = _globalToLocal.OfPoint(FixturePoint);

        _condition = PlacementCondition.Up;

        (_symbolLength, _symbolWidth) = CalculateSymbolDimensions(view, fixture, sourceTag);
    }

    private static XYZ GetCurveMidpoint(FamilyInstance fixture)
    {
        var locationCurve = (LocationCurve)fixture.Location;
        return locationCurve.Curve.Evaluate(0.5, true);
    }

    private static bool DetermineRotationMode(IndependentTag tag)
    {
        var param = tag.LookupParameter("Orientation");
        if (param == null) return false;

        var value = param.AsValueString();
        return value == "Horizontal" || value == "Vertical";
    }

    private static (double length, double width) CalculateSymbolDimensions(View view, FamilyInstance fixture, IndependentTag tag)
    {
        var (length, width) = GeometryHelper.GetSymbolExtents(fixture, view, BubbleConstants.DefaultSymbolSizeFt);

        width = Math.Max(width, BubbleConstants.MinSymbolWidthFt);

        var tagWidth = GetTagWidth(view, tag);
        width = Math.Max(width, tagWidth);

        return (length, width);
    }

    private static double GetTagWidth(View view, IndependentTag tag)
    {
        var charCount = tag.TagText?.Length ?? 0;

        return charCount switch
        {
            2 => BubbleConstants.TagWidth2CharsFt,
            3 => BubbleConstants.TagWidth3CharsFt,
            _ => CalculateTagWidthFromBounds(view, tag)
        };
    }

    private static double CalculateTagWidthFromBounds(View view, IndependentTag tag)
    {
        var bbox = tag.get_BoundingBox(view);
        return bbox != null ? Math.Abs(bbox.Max.X - bbox.Min.X) : 0;
    }

    public void CalculateFinalPositions(XYZ flipPoint)
    {
        var flipLocal = _globalToLocal.OfPoint(flipPoint);

        _condition = flipLocal.Y >= _connectorLocal.Y
            ? PlacementCondition.Up
            : PlacementCondition.Down;

        var flipState = DetermineFlipState(flipLocal);
        IsFlipped = flipState;

        var offsets = GetOffsetsForLineBased(_condition, _symbolLength, _symbolWidth);
        ApplyFlip(ref offsets, flipState);

        var newTagLocal = CalculateTagPosition(offsets);
        var vertex2Local = CalculateVertex2(offsets);
        var vertex3Local = CalculateVertex3(newTagLocal, offsets);

        NewTagPosition = _localToGlobal.OfPoint(newTagLocal);
        Vertex2 = _localToGlobal.OfPoint(vertex2Local);
        Vertex3 = _localToGlobal.OfPoint(vertex3Local);
    }

    private bool DetermineFlipState(XYZ flipLocal)
    {
        return flipLocal.X < _connectorLocal.X;
    }

    private PlacementOffsets GetOffsetsForLineBased(
        PlacementCondition condition,
        double symbolLength,
        double symbolWidth)
    {
        var halfWidth = symbolWidth * 0.5;

        var tagOffsetAlongFixture = 14.0 * BubbleConstants.InchesToFeet;
        var tagOffsetPerpendicular = 6.25 * BubbleConstants.InchesToFeet;

        var tagXDirection = 1;

        var isOutward = condition == PlacementCondition.Up;

        if (isOutward)
        {
            return new PlacementOffsets
            {
                V2X = 0,
                V2Y = tagOffsetPerpendicular,
                V3X = -BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = tagXDirection * tagOffsetAlongFixture,
                TagY = tagOffsetPerpendicular,
                IsHorizontalCondition = true
            };
        }
        else
        {
            return new PlacementOffsets
            {
                V2X = 0,
                V2Y = -tagOffsetPerpendicular,
                V3X = -BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = tagXDirection * tagOffsetAlongFixture,
                TagY = -tagOffsetPerpendicular,
                IsHorizontalCondition = true
            };
        }
    }

    private static void ApplyFlip(ref PlacementOffsets offsets, bool flipState)
    {
        if (!flipState) return;

        offsets.V2X = -offsets.V2X;
        offsets.V3X = -offsets.V3X;
        offsets.TagX = -offsets.TagX;
    }

    private XYZ CalculateTagPosition(PlacementOffsets offsets)
    {
        return new XYZ(
            _connectorLocal.X + offsets.TagX,
            _connectorLocal.Y + offsets.TagY,
            _connectorLocal.Z);
    }

    private XYZ CalculateVertex2(PlacementOffsets offsets)
    {
        return new XYZ(
            _connectorLocal.X + offsets.V2X,
            _connectorLocal.Y + offsets.V2Y,
            _connectorLocal.Z);
    }

    private XYZ CalculateVertex3(XYZ tagLocal, PlacementOffsets offsets)
    {
        return new XYZ(
            tagLocal.X + offsets.V3X,
            tagLocal.Y,
            _connectorLocal.Z);
    }
}
