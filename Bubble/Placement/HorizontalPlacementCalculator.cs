using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Models;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Handles placement calculations for horizontal fixtures (ceiling/floor-based).
/// </summary>
internal class HorizontalPlacementCalculator : PlacementCalculatorBase
{
    private readonly bool _hasRemotePowerSupply;
    private XYZ _flipLocal = XYZ.Zero;

    public HorizontalPlacementCalculator(Document doc, View view, FamilyInstance fixture,
        IndependentTag sourceTag, bool hasRemotePowerSupply = false)
        : base(doc, view, fixture, sourceTag)
    {
        _hasRemotePowerSupply = hasRemotePowerSupply;
    }

    public override void CalculateFinalPositions(XYZ flipPoint)
    {
        var flipLocal = GlobalToLocal.OfPoint(flipPoint);
        _flipLocal = flipLocal;
        var flipState = DetermineFlipState(flipLocal);
        IsFlipped = flipState;

        var offsets = GetOffsetsForCondition(Condition, SymbolLength, SymbolWidth);
        ApplyFlip(ref offsets, flipState);

        var newTagLocal = CalculateTagPosition(offsets);
        var vertex2Local = CalculateVertex2(offsets);

        // V3 uses the base tag position (without RPS offset) so the wire elbow
        // stays aligned with the wire path rather than shifting with the tag.
        var wireAnchorLocal = newTagLocal;
        if (_hasRemotePowerSupply && !offsets.IsHorizontalCondition)
        {
            var rpsOffset = Math.Sign(offsets.TagX) * BubbleConstants.RemoteSwitchlegExtraXOffsetFt;
            wireAnchorLocal = new XYZ(newTagLocal.X - rpsOffset, newTagLocal.Y, newTagLocal.Z);
        }
        var vertex3Local = CalculateVertex3(wireAnchorLocal, offsets);

        NewTagPosition = LocalToGlobal.OfPoint(newTagLocal);
        Vertex2 = LocalToGlobal.OfPoint(vertex2Local);
        Vertex3 = LocalToGlobal.OfPoint(vertex3Local);
    }

    private bool DetermineFlipState(XYZ flipLocal)
    {
        return Condition is PlacementCondition.Right or PlacementCondition.Left
            ? flipLocal.Y < TagLocal.Y
            : flipLocal.X < TagLocal.X;
    }

    private PlacementOffsets GetOffsetsForCondition(PlacementCondition condition, double symbolLength, double symbolWidth)
    {
        var halfLength = symbolLength * 0.5;
        var halfWidth = symbolWidth * 0.5;

        return condition switch
        {
            PlacementCondition.Right => new PlacementOffsets
            {
                V2X = -BubbleConstants.WireHorizontalOffsetFt,
                V2Y = halfLength + BubbleConstants.WireToSymbolGapFt,
                V3X = -BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = 0,
                TagY = halfLength + BubbleConstants.TagOffsetVerticalFt,
                IsHorizontalCondition = true
            },
            PlacementCondition.Left => new PlacementOffsets
            {
                V2X = BubbleConstants.WireHorizontalOffsetFt,
                V2Y = halfLength + BubbleConstants.WireToSymbolGapFt,
                V3X = BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = 0,
                TagY = halfLength + BubbleConstants.TagOffsetVerticalFt,
                IsHorizontalCondition = true
            },
            PlacementCondition.Up => new PlacementOffsets
            {
                V2X = halfWidth + BubbleConstants.WireToSymbolGapHorizontalFt,
                V2Y = -BubbleConstants.WireVerticalOffsetFt,
                V3X = -BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = halfWidth + BubbleConstants.TagOffsetHorizontalFt,
                TagY = 0,
                IsHorizontalCondition = false
            },
            _ => new PlacementOffsets // Down
            {
                V2X = halfWidth + BubbleConstants.WireToSymbolGapHorizontalFt,
                V2Y = BubbleConstants.WireVerticalOffsetFt,
                V3X = -BubbleConstants.WireElbowOffsetFt,
                V3Y = 0,
                TagX = halfWidth + BubbleConstants.TagOffsetHorizontalFt,
                TagY = 0,
                IsHorizontalCondition = false
            }
        };
    }

    private static void ApplyFlip(ref PlacementOffsets offsets, bool flipState)
    {
        if (!flipState) return;

        if (offsets.IsHorizontalCondition)
        {
            offsets.V2Y = -offsets.V2Y;
            offsets.TagY = -offsets.TagY;
        }
        else
        {
            offsets.V2X = -offsets.V2X;
            offsets.V3X = -offsets.V3X;
            offsets.TagX = -offsets.TagX;
        }
    }

    private XYZ CalculateTagPosition(PlacementOffsets offsets)
    {
        if (offsets.IsHorizontalCondition)
        {
            var baseOffset = BubbleConstants.TagXOffsetFt
                + (_hasRemotePowerSupply ? BubbleConstants.RemoteSwitchlegExtraXOffsetFt : 0);
            var tagXOffset = Math.Sign(TagLocal.X - FixtureLocal.X) * baseOffset;
            return new XYZ(
                FixtureLocal.X + tagXOffset,
                FixtureLocal.Y + offsets.TagY,
                FixtureLocal.Z);
        }

        var extraX = _hasRemotePowerSupply
            ? Math.Sign(offsets.TagX) * BubbleConstants.RemoteSwitchlegExtraXOffsetFt
            : 0;

        return new XYZ(
            FixtureLocal.X + offsets.TagX + extraX,
            TagLocal.Y,
            FixtureLocal.Z);
    }

    public bool DetermineEffectiveFlipForRPS()
    {
        return Condition switch
        {
            PlacementCondition.Left => false,
            PlacementCondition.Right => true,
            _ => !IsFlipped
        };
    }

    private XYZ CalculateVertex2(PlacementOffsets offsets)
    {
        return new XYZ(
            FixtureLocal.X + offsets.V2X,
            FixtureLocal.Y + offsets.V2Y,
            FixtureLocal.Z);
    }

    private XYZ CalculateVertex3(XYZ tagLocal, PlacementOffsets offsets)
    {
        var y = offsets.IsHorizontalCondition
            ? tagLocal.Y
            : tagLocal.Y + offsets.V3Y;

        return new XYZ(tagLocal.X + offsets.V3X, y, FixtureLocal.Z);
    }
}
