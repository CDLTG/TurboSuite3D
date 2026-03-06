using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Helpers;
using TurboSuite.Tag.Constants;
using TurboSuite.Tag.Helpers;
using TurboSuite.Tag.Models;
using TurboSuite.Tag.Services;

namespace TurboSuite.Tag;

[Transaction(TransactionMode.Manual)]
public class TagCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        try
        {
            if (!IsValidViewType(doc.ActiveView))
            {
                TaskDialog.Show("TurboTag", "Active view must be a Floor Plan or Reflected Ceiling Plan.");
                return Result.Cancelled;
            }

            var selectedFixtures = FixtureSelectionService.GetSelectedLightingFixtures(doc, uidoc.Selection.GetElementIds());
            if (selectedFixtures.Count == 0)
            {
                TaskDialog.Show("TurboTag", "No lighting fixtures selected.\nSelect at least one lighting fixture.");
                return Result.Cancelled;
            }

            var faceBasedFixtures = selectedFixtures.Where(f => GeometryHelper.IsOnVerticalFace(f) || GeometryHelper.IsWallSconce(f) || GeometryHelper.IsVerticalFamily(f)).ToList();
            var lineBasedFixtures = selectedFixtures.Where(f => !GeometryHelper.IsOnVerticalFace(f) && !GeometryHelper.IsWallSconce(f) && !GeometryHelper.IsVerticalFamily(f) && GeometryHelper.IsLineBasedFixture(f)).ToList();
            var pointBasedFixtures = selectedFixtures.Where(f => !GeometryHelper.IsOnVerticalFace(f) && !GeometryHelper.IsWallSconce(f) && !GeometryHelper.IsVerticalFamily(f) && !GeometryHelper.IsLineBasedFixture(f)).ToList();

            int totalTagged = 0;

            if (faceBasedFixtures.Count > 0)
            {
                FamilySymbol? tagType = TagTypeService.GetTagType(doc);
                if (tagType == null)
                {
                    TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.TagFamilyName}' not found.\nLoad this tag family into the project.");
                    return Result.Cancelled;
                }

                totalTagged += PlaceTagsFaceBased(doc, faceBasedFixtures, tagType);
            }

            if (lineBasedFixtures.Count > 0)
            {
                TagDirection linearDirection = PromptForDirectionLinear();
                if (linearDirection == TagDirection.None)
                {
                    return Result.Cancelled;
                }

                string linearTypeName = linearDirection == TagDirection.Up ? "Tag_Top" : "Tag_Bottom";
                FamilySymbol? linearTagType = TagTypeService.GetLinearTagType(doc, linearTypeName);
                if (linearTagType == null)
                {
                    TaskDialog.Show("TurboTag", $"Tag type '{linearTypeName}' in family '{TagConstants.LinearTagFamilyName}' not found.\nLoad this tag family into the project.");
                    return Result.Cancelled;
                }

                totalTagged += PlaceTags(doc, lineBasedFixtures, linearTagType, linearDirection, true);
            }

            if (pointBasedFixtures.Count > 0)
            {
                FamilySymbol? tagType = TagTypeService.GetTagType(doc);
                if (tagType == null)
                {
                    TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.TagFamilyName}' not found.\nLoad this tag family into the project.");
                    return Result.Cancelled;
                }

                TagDirection direction = PromptForDirection();
                if (direction == TagDirection.None)
                {
                    return Result.Cancelled;
                }

                totalTagged += PlaceTags(doc, pointBasedFixtures, tagType, direction);
            }

            if (selectedFixtures.Count > 10)
            {
                TaskDialog.Show("TurboTag", $"Successfully tagged {totalTagged} of {selectedFixtures.Count} fixtures.");
            }

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("TurboTag Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }

    private static bool IsValidViewType(View view)
    {
        return view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan;
    }

    private static TagDirection PromptForDirectionLinear()
        => PromptForDirection("Select tag placement direction for line-based families:", includeLeftRight: false);

    private static TagDirection PromptForDirection()
        => PromptForDirection("Select tag placement direction for point-based families:", includeLeftRight: true);

    private static TagDirection PromptForDirection(string instruction, bool includeLeftRight)
    {
        var dialog = new TaskDialog("Tag Direction")
        {
            MainInstruction = instruction,
            CommonButtons = TaskDialogCommonButtons.Cancel
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Up");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Down");
        if (includeLeftRight)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Right");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Left");
        }

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => TagDirection.Up,
            TaskDialogResult.CommandLink2 => TagDirection.Down,
            TaskDialogResult.CommandLink3 when includeLeftRight => TagDirection.Right,
            TaskDialogResult.CommandLink4 when includeLeftRight => TagDirection.Left,
            _ => TagDirection.None
        };
    }

    private void DeleteExistingTags(Document doc, ElementId fixtureId, ElementId viewId, string tagFamilyName)
    {
        var tagsToDelete = new FilteredElementCollector(doc, viewId)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>()
            .Where(tag =>
            {
                if (!tag.GetTaggedLocalElementIds().Contains(fixtureId))
                    return false;

                ElementId typeId = tag.GetTypeId();
                if (typeId == ElementId.InvalidElementId)
                    return false;

                FamilySymbol? tagSymbol = doc.GetElement(typeId) as FamilySymbol;
                return tagSymbol != null &&
                       string.Equals(tagSymbol.FamilyName, tagFamilyName, StringComparison.OrdinalIgnoreCase);
            })
            .Select(tag => tag.Id)
            .ToList();

        if (tagsToDelete.Count > 0)
        {
            doc.Delete(tagsToDelete);
        }
    }

    private int PlaceTags(Document doc, List<FamilyInstance> fixtures, FamilySymbol tagType, TagDirection direction, bool isLineBased = false)
    {
        int successCount = 0;
        View activeView = doc.ActiveView;
        ElementId tagTypeId = tagType.Id;
        ElementId viewId = activeView.Id;
        string tagFamilyName = tagType.FamilyName;

        using (var trans = new Transaction(doc, "TurboTag - Place Tags"))
        {
            var failureOptions = trans.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new TagFailurePreprocessor());
            trans.SetFailureHandlingOptions(failureOptions);

            trans.Start();

            foreach (FamilyInstance fixture in fixtures)
            {
                DeleteExistingTags(doc, fixture.Id, viewId, tagFamilyName);

                if (TryPlaceTag(doc, fixture, tagTypeId, viewId, direction, isLineBased))
                {
                    successCount++;
                }
            }

            trans.Commit();
        }

        return successCount;
    }

    private int PlaceTagsFaceBased(Document doc, List<FamilyInstance> fixtures, FamilySymbol tagType)
    {
        int successCount = 0;
        View activeView = doc.ActiveView;
        ElementId tagTypeId = tagType.Id;
        ElementId viewId = activeView.Id;
        string tagFamilyName = tagType.FamilyName;

        using (var trans = new Transaction(doc, "TurboTag - Place Tags"))
        {
            var failureOptions = trans.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new TagFailurePreprocessor());
            trans.SetFailureHandlingOptions(failureOptions);

            trans.Start();

            foreach (FamilyInstance fixture in fixtures)
            {
                DeleteExistingTags(doc, fixture.Id, viewId, tagFamilyName);

                if (TryPlaceTagFaceBased(doc, fixture, tagTypeId, viewId))
                {
                    successCount++;
                }
            }

            trans.Commit();
        }

        return successCount;
    }

    private bool TryPlaceTagFaceBased(Document doc, FamilyInstance fixture, ElementId tagTypeId, ElementId viewId)
    {
        try
        {
            if (fixture.Location is not LocationPoint locPoint)
                return false;

            XYZ fixtureLocation = locPoint.Point;

            var reference = new Reference(fixture);
            IndependentTag? tag = IndependentTag.Create(
                doc, tagTypeId, viewId, reference,
                addLeader: false,
                TagOrientation.Horizontal,
                fixtureLocation);

            if (tag == null)
                return false;

            XYZ offsetDirection = XYZ.Zero;
            Reference? hostFaceRef = fixture.HostFace;
            if (hostFaceRef != null)
            {
                Element? host = fixture.Host;

                if (host is RevitLinkInstance linkInstance)
                {
                    Document? linkedDoc = linkInstance.GetLinkDocument();
                    if (linkedDoc != null)
                    {
                        GeometryObject? geomObj = linkedDoc.GetElement(hostFaceRef.LinkedElementId)
                            ?.GetGeometryObjectFromReference(hostFaceRef.CreateReferenceInLink());

                        if (geomObj is PlanarFace planarFace)
                        {
                            Transform linkTransform = linkInstance.GetTotalTransform();
                            XYZ faceNormal = linkTransform.OfVector(planarFace.FaceNormal);
                            offsetDirection = new XYZ(faceNormal.X, faceNormal.Y, 0).Normalize();
                        }
                    }
                }
                else if (host != null)
                {
                    GeometryObject? geomObj = host.GetGeometryObjectFromReference(hostFaceRef);

                    if (geomObj is PlanarFace planarFace)
                    {
                        XYZ faceNormal = planarFace.FaceNormal;
                        offsetDirection = new XYZ(faceNormal.X, faceNormal.Y, 0).Normalize();
                    }
                }
            }

            if (offsetDirection.IsZeroLength())
            {
                // Fallback for unhosted sconces: use FacingOrientation as wall normal
                XYZ facing = fixture.FacingOrientation;
                XYZ horizontal = new XYZ(facing.X, facing.Y, 0);
                if (horizontal.GetLength() > 0.001)
                    offsetDirection = horizontal.Normalize();
            }

            if (!offsetDirection.IsZeroLength())
            {
                double symbolExtent = GeometryHelper.GetSymbolExtentInDirection(
                    fixture, doc.ActiveView, offsetDirection, TagConstants.DefaultSymbolSizeFeet);
                double offsetDistance = symbolExtent + TagConstants.VerticalOffsetFeet;

                XYZ globalOffset = offsetDirection * offsetDistance;
                ElementTransformUtils.MoveElement(doc, tag.Id, globalOffset);
            }

            return true;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryPlaceTag(Document doc, FamilyInstance fixture, ElementId tagTypeId, ElementId viewId, TagDirection direction, bool isLineBased = false)
    {
        try
        {
            XYZ fixtureLocation;

            if (isLineBased)
            {
                if (fixture.Location is not LocationCurve locCurve)
                    return false;

                Curve curve = locCurve.Curve;
                fixtureLocation = curve.Evaluate(0.5, true);
            }
            else
            {
                if (fixture.Location is not LocationPoint locPoint)
                    return false;

                fixtureLocation = locPoint.Point;
            }

            var reference = new Reference(fixture);
            IndependentTag? tag = IndependentTag.Create(
                doc, tagTypeId, viewId, reference,
                addLeader: false,
                TagOrientation.Horizontal,
                fixtureLocation);

            if (tag == null)
                return false;

            XYZ localOffset;

            if (isLineBased)
            {
                bool isReversed = TagPlacementService.IsLineReversed(fixture);
                localOffset = TagPlacementService.CalculateLinearOffset(direction, isReversed);
            }
            else
            {
                var (symbolLength, symbolWidth) = GeometryHelper.GetSymbolExtents(fixture, doc.ActiveView, TagConstants.DefaultSymbolSizeFeet);
                double tagWidth = TagPlacementService.EstimateTagWidth(tag, doc, viewId);
                localOffset = TagPlacementService.CalculateOffset(direction, symbolLength, symbolWidth, tagWidth);
            }

            XYZ globalOffset = TagPlacementService.TransformToGlobal(fixture, localOffset);

            if (!globalOffset.IsZeroLength())
            {
                ElementTransformUtils.MoveElement(doc, tag.Id, globalOffset);
            }

            return true;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
