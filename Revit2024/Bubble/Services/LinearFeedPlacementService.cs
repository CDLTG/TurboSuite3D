using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Placement;

namespace TurboSuite.Bubble.Services;

/// <summary>
/// Places a static "linear feed" combo (detail-item leader + tag) for line-based RPS fixtures
/// when dynamic driver tags are disabled. The leader is placed 1" off the fixture line on the
/// "down" side; if the user clicked "up", it is mirrored across the fixture line.
/// </summary>
internal static class LinearFeedPlacementService
{
    public static void Place(
        Document doc,
        View view,
        FamilyInstance fixture,
        LineBasedPlacementCalculator placement,
        ElementId tagTypeId,
        FamilySymbol detailSymbol,
        bool isUp)
    {
        using var trans = new Transaction(doc, "TurboBubble - Linear Feed");
        trans.Start();

        if (!detailSymbol.IsActive) detailSymbol.Activate();

        var rotation = placement.Rotation;
        var lineDir = new XYZ(Math.Cos(rotation), Math.Sin(rotation), 0);
        // Family-local +Y direction in world: 90° CCW from lineDir. This is the same axis
        // the calculator uses to compute IsUp (click on local +Y side), so they line up
        // regardless of which way the fixture line was drawn.
        var perpDir = new XYZ(-Math.Sin(rotation), Math.Cos(rotation), 0);

        // Line-based detail items must lie in the view's drafting plane.
        var viewZ = view.Origin.Z;
        var connector = new XYZ(placement.FixturePoint.X, placement.FixturePoint.Y, viewZ);

        // Detail leader anchor: 1" off the fixture line on the family-default ("down") side.
        // We always place in the default state, then mirror to the +Y side if user clicked "up".
        var detailStart = connector - perpDir * BubbleConstants.LinearFeedDetailPerpOffsetFt;
        var detailEnd = detailStart + lineDir * BubbleConstants.LinearFeedDetailLengthFt;

        var placementType = detailSymbol.Family.FamilyPlacementType;
        FamilyInstance detail;
        if (placementType == FamilyPlacementType.CurveBasedDetail)
        {
            var line = Line.CreateBound(detailStart, detailEnd);
            detail = doc.Create.NewFamilyInstance(line, detailSymbol, view);
        }
        else if (placementType == FamilyPlacementType.ViewBased)
        {
            detail = doc.Create.NewFamilyInstance(detailStart, detailSymbol, view);
            doc.Regenerate();
            if (Math.Abs(rotation) > BubbleConstants.RotationEpsilon)
            {
                var axis = Line.CreateBound(detailStart, detailStart + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, detail.Id, axis, rotation);
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Detail family '{detailSymbol.FamilyName}' has unsupported FamilyPlacementType '{placementType}'. " +
                $"Expected ViewBased or CurveBasedDetail.");
        }
        doc.Regenerate();

        if (isUp)
        {
            // Mirror plane contains the fixture line (passes through the connector); normal is perpendicular in plan.
            var mirrorPlane = Plane.CreateByNormalAndOrigin(perpDir, connector);
            ElementTransformUtils.MirrorElements(
                doc, new[] { detail.Id }, mirrorPlane, mirrorCopies: false);
        }

        // Tag: past the end of the leader (parallel) and offset off the fixture line (perpendicular),
        // on the same side as the user's click.
        var perpSign = isUp ? +1.0 : -1.0;
        var tagPosition = connector
            + lineDir * (BubbleConstants.LinearFeedDetailLengthFt + BubbleConstants.LinearFeedTagParallelGapFt)
            + perpDir * (BubbleConstants.LinearFeedTagPerpOffsetFt * perpSign);

        var tag = IndependentTag.Create(
            doc,
            tagTypeId,
            view.Id,
            new Reference(fixture),
            addLeader: false,
            TagOrientation.Horizontal,
            tagPosition);

        if (Math.Abs(rotation) > BubbleConstants.RotationEpsilon)
        {
            var tagAxis = Line.CreateBound(tagPosition, tagPosition + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, tag.Id, tagAxis, rotation);
        }

        trans.Commit();
    }
}
