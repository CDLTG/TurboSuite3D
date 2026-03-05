using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Handles placement calculations for vertical face-based fixtures (wall-mounted).
/// Uses wall-based coordinate system where:
/// - X axis = wall parallel direction (along the wall)
/// - Y axis = wall normal direction (perpendicular to wall, pointing outward)
/// </summary>
internal class VerticalFacePlacementCalculator : IPlacementCalculator
{
    public XYZ FixturePoint { get; }
    public double Rotation { get; }
    public bool RotatesWithComponent { get; }

    public XYZ NewTagPosition { get; private set; } = XYZ.Zero;
    public XYZ Vertex2 { get; private set; } = XYZ.Zero;
    public XYZ Vertex3 { get; private set; } = XYZ.Zero;
    public bool IsFlipped { get; private set; }

    private readonly XYZ _wallNormal;
    private readonly XYZ _wallParallel;
    private readonly Transform _globalToWallLocal;
    private readonly Transform _wallLocalToGlobal;

    private readonly XYZ _fixtureLocal;
    private readonly XYZ _tagLocal;
    private readonly PlacementCondition _condition;
    private readonly double _symbolLength;
    private readonly double _symbolWidth;
    private readonly bool _isWallSconce;

    public VerticalFacePlacementCalculator(Document doc, View view, FamilyInstance fixture, IndependentTag sourceTag)
    {
        FixturePoint = GeometryHelper.GetFixtureLocation(fixture)
            ?? throw new InvalidOperationException("Fixture has no valid location for vertical face placement.");
        var tagPoint = sourceTag.TagHeadPosition;

        RotatesWithComponent = PlacementCalculatorBase.DetermineRotationMode(sourceTag);

        _wallNormal = GeometryHelper.GetWallFaceNormal(fixture);
        _wallParallel = new XYZ(-_wallNormal.Y, _wallNormal.X, 0);

        var wallAngle = Math.Atan2(_wallNormal.Y, _wallNormal.X) - Math.PI / 2.0;
        Rotation = wallAngle;

        _globalToWallLocal = Transform.CreateRotationAtPoint(XYZ.BasisZ, -wallAngle, FixturePoint);
        _wallLocalToGlobal = _globalToWallLocal.Inverse;

        _fixtureLocal = _globalToWallLocal.OfPoint(FixturePoint);
        _tagLocal = _globalToWallLocal.OfPoint(tagPoint);

        _condition = DetermineCondition(_fixtureLocal, _tagLocal);

        (_symbolLength, _symbolWidth) = PlacementCalculatorBase.CalculateSymbolDimensions(view, fixture, sourceTag);

        _isWallSconce = GeometryHelper.IsWallSconce(fixture);
    }

    private static PlacementCondition DetermineCondition(XYZ fixtureLocal, XYZ tagLocal)
    {
        var diffY = tagLocal.Y - fixtureLocal.Y;
        return diffY >= 0 ? PlacementCondition.Up : PlacementCondition.Down;
    }

    public void CalculateFinalPositions(XYZ flipPoint)
    {
        var flipLocal = _globalToWallLocal.OfPoint(flipPoint);
        var flipState = DetermineFlipState(flipLocal);
        IsFlipped = flipState;

        var offsets = GetOffsetsForVerticalFixture(_condition, _symbolLength, _symbolWidth);
        ApplyFlip(ref offsets, flipState);

        var newTagLocal = CalculateTagPosition(offsets);
        var vertex2Local = CalculateVertex2(offsets);
        var vertex3Local = CalculateVertex3(newTagLocal, offsets);

        NewTagPosition = _wallLocalToGlobal.OfPoint(newTagLocal);
        Vertex2 = _wallLocalToGlobal.OfPoint(vertex2Local);
        Vertex3 = _wallLocalToGlobal.OfPoint(vertex3Local);
    }

    private bool DetermineFlipState(XYZ flipLocal)
    {
        var tagXDirection = Math.Sign(_tagLocal.X - _fixtureLocal.X);
        if (tagXDirection == 0) tagXDirection = 1;
        var flipDirection = Math.Sign(flipLocal.X - _tagLocal.X);
        if (flipDirection == 0) return false;
        return flipDirection != tagXDirection;
    }

    private PlacementOffsets GetOffsetsForVerticalFixture(
        PlacementCondition condition,
        double symbolLength,
        double symbolWidth)
    {
        var halfWidth = symbolWidth * 0.5;
        var halfLength = symbolLength * 0.5;

        var tagOffsetAlongWall = halfWidth + (8.25 * BubbleConstants.InchesToFeet);
        var tagOffsetFromWall = 5.5 * BubbleConstants.InchesToFeet;

        var wireGapHorizontal = _isWallSconce ? BubbleConstants.WireToSymbolGapWallSconceFt : BubbleConstants.WireToSymbolGapHorizontalFt;

        var tagXDirection = Math.Sign(_tagLocal.X - _fixtureLocal.X);
        if (tagXDirection == 0) tagXDirection = 1;

        var isOutward = condition == PlacementCondition.Up || condition == PlacementCondition.Right;

        if (isOutward)
        {
            return new PlacementOffsets
            {
                V2X = tagXDirection * (halfWidth + wireGapHorizontal),
                V2Y = BubbleConstants.WireVerticalOffsetFt,
                V3X = -tagXDirection * BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = tagXDirection * tagOffsetAlongWall,
                TagY = tagOffsetFromWall,
                IsHorizontalCondition = true
            };
        }
        else
        {
            return new PlacementOffsets
            {
                V2X = tagXDirection * (halfWidth + wireGapHorizontal),
                V2Y = -BubbleConstants.WireVerticalOffsetFt,
                V3X = -tagXDirection * BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = tagXDirection * tagOffsetAlongWall,
                TagY = -tagOffsetFromWall,
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
            _fixtureLocal.X + offsets.TagX,
            _fixtureLocal.Y + offsets.TagY,
            _fixtureLocal.Z);
    }

    private XYZ CalculateVertex2(PlacementOffsets offsets)
    {
        return new XYZ(
            _fixtureLocal.X + offsets.V2X,
            _fixtureLocal.Y + offsets.V2Y,
            _fixtureLocal.Z);
    }

    private XYZ CalculateVertex3(XYZ tagLocal, PlacementOffsets offsets)
    {
        var y = offsets.IsHorizontalCondition
            ? tagLocal.Y
            : tagLocal.Y + offsets.V3Y;

        return new XYZ(tagLocal.X + offsets.V3X, y, _fixtureLocal.Z);
    }
}
