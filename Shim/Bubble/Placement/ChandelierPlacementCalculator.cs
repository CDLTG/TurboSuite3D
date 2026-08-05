using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Models;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Placement calculator for chandelier families. The bubble lands at one of four
/// diagonal corners, picked by combining the type tag's side (Condition) with the
/// user click. The wire is anchored to the connector and Revit auto-clips it at
/// the circle's detail lines.
/// </summary>
internal class ChandelierPlacementCalculator : PlacementCalculatorBase
{
    public ChandelierPlacementCalculator(Document doc, View view, FamilyInstance fixture, IndependentTag sourceTag)
        : base(doc, view, fixture, sourceTag)
    {
    }

    public override void CalculateFinalPositions(XYZ flipPoint)
    {
        var flipLocal = GlobalToLocal.OfPoint(flipPoint);
        IsFlipped = DetermineFlipState(flipLocal);

        var (sx, sy) = GetCornerSigns(Condition, IsFlipped);

        var angleRad = BubbleConstants.ChandelierBubbleAngleDegrees * Math.PI / 180.0;
        var symbolHalf = Math.Max(SymbolLength, SymbolWidth) * 0.5;
        var d = symbolHalf + BubbleConstants.ChandelierBubbleOffsetExtensionFt;

        var dirX = sx * Math.Cos(angleRad);
        var dirY = sy * Math.Sin(angleRad);

        // Perpendicular = 90° CW rotation of (dirX, dirY). Flip if it points toward the
        // type tag, so V2's kick always pushes the arc AWAY from the tag (arc center
        // ends up on the tag's side, bubble bulges away).
        var perpX = dirY;
        var perpY = -dirX;
        var tagDirX = TagLocal.X - FixtureLocal.X;
        var tagDirY = TagLocal.Y - FixtureLocal.Y;
        if (perpX * tagDirX + perpY * tagDirY > 0)
        {
            perpX = -perpX;
            perpY = -perpY;
        }

        // Bubble at the diagonal corner, plus a horizontal-only nudge further away from
        // the fixture (along the corner's X sign, so it never lifts the bubble vertically).
        var newTagLocal = new XYZ(
            FixtureLocal.X + d * dirX + sx * BubbleConstants.ChandelierBubbleHorizontalNudgeFt,
            FixtureLocal.Y + d * dirY,
            FixtureLocal.Z);

        // V3: elbow inset back from bubble along the diagonal (mirrors default WireElbowOffset)
        var v3Local = new XYZ(
            newTagLocal.X - BubbleConstants.ChandelierWireElbowOffsetFt * dirX,
            newTagLocal.Y - BubbleConstants.ChandelierWireElbowOffsetFt * dirY,
            FixtureLocal.Z);

        // V2: just past symbol edge along diagonal, with perpendicular kick for arc curvature
        // (mirrors default V2 = fixture + halfSymbol + gap + perpendicular kick)
        var v2ParallelDist = symbolHalf + BubbleConstants.ChandelierWireV2GapFt;
        var v2Local = new XYZ(
            FixtureLocal.X + v2ParallelDist * dirX + BubbleConstants.ChandelierWireV2PerpKickFt * perpX,
            FixtureLocal.Y + v2ParallelDist * dirY + BubbleConstants.ChandelierWireV2PerpKickFt * perpY,
            FixtureLocal.Z);

        NewTagPosition = LocalToGlobal.OfPoint(newTagLocal);
        Vertex2 = LocalToGlobal.OfPoint(v2Local);
        Vertex3 = LocalToGlobal.OfPoint(v3Local);
    }

    private bool DetermineFlipState(XYZ flipLocal)
    {
        return Condition is PlacementCondition.Right or PlacementCondition.Left
            ? flipLocal.Y < TagLocal.Y
            : flipLocal.X < TagLocal.X;
    }

    private static (double sx, double sy) GetCornerSigns(PlacementCondition condition, bool flipped)
    {
        return condition switch
        {
            PlacementCondition.Up    => flipped ? (-1.0, +1.0) : (+1.0, +1.0), // 4 / 1
            PlacementCondition.Down  => flipped ? (-1.0, -1.0) : (+1.0, -1.0), // 3 / 2
            PlacementCondition.Right => flipped ? (+1.0, -1.0) : (+1.0, +1.0), // 2 / 1
            PlacementCondition.Left  => flipped ? (-1.0, -1.0) : (-1.0, +1.0), // 3 / 4
            _ => (+1.0, +1.0)
        };
    }
}
