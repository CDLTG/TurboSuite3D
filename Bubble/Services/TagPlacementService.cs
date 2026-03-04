using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Placement;

namespace TurboSuite.Bubble.Services;

/// <summary>
/// Service for creating and configuring switchleg tags.
/// </summary>
internal static class TagPlacementService
{
    public static IndependentTag CreateAndConfigureTag(
        Document doc,
        View view,
        FamilyInstance fixture,
        IndependentTag sourceTag,
        IPlacementCalculator placement,
        ElementId tagTypeId)
    {
        var newTag = IndependentTag.Create(
            doc,
            view.Id,
            new Reference(fixture),
            false,
            TagMode.TM_ADDBY_CATEGORY,
            TagOrientation.Horizontal,
            placement.NewTagPosition);

        newTag.ChangeTypeId(tagTypeId);
        newTag.TagHeadPosition = placement.NewTagPosition;

        ApplyTagRotation(doc, newTag, sourceTag, placement);

        return newTag;
    }

    public static void ApplyTagRotation(
        Document doc,
        IndependentTag newTag,
        IndependentTag sourceTag,
        IPlacementCalculator placement)
    {
        if (placement.RotatesWithComponent)
        {
            CopyOrientationParameter(sourceTag, newTag);
            RotateTagToMatchFixture(doc, newTag, placement);
        }
        else
        {
            SetTagToModelOrientation(newTag, placement.Rotation);
        }
    }

    private static void CopyOrientationParameter(IndependentTag source, IndependentTag target)
    {
        var srcParam = source.LookupParameter("Orientation");
        var dstParam = target.LookupParameter("Orientation");

        if (srcParam != null && dstParam != null && !dstParam.IsReadOnly)
            dstParam.Set(srcParam.AsInteger());
    }

    private static void RotateTagToMatchFixture(Document doc, IndependentTag tag, IPlacementCalculator placement)
    {
        if (Math.Abs(placement.Rotation) <= BubbleConstants.RotationEpsilon) return;

        var axis = Line.CreateBound(
            placement.NewTagPosition,
            placement.NewTagPosition + XYZ.BasisZ * 10);

        ElementTransformUtils.RotateElement(doc, tag.Id, axis, placement.Rotation);
    }

    private static void SetTagToModelOrientation(IndependentTag tag, double rotation)
    {
        var orientationParam = tag.LookupParameter("Orientation");
        if (orientationParam != null && !orientationParam.IsReadOnly)
            orientationParam.Set(2); // Model orientation

        var angleParam = tag.LookupParameter("Angle");
        if (angleParam != null && !angleParam.IsReadOnly)
            angleParam.Set(rotation);
    }
}
