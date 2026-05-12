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
using TurboSuite.Tag.Views;

namespace TurboSuite.Tag;

[Transaction(TransactionMode.Manual)]
public class TagCommand : IExternalCommand
{
    private IntPtr _revitHandle;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        _revitHandle = commandData.Application.MainWindowHandle;

        try
        {
            if (!IsValidViewType(doc.ActiveView))
            {
                TaskDialog.Show("TurboTag", "Active view must be a Floor Plan or Reflected Ceiling Plan.");
                return Result.Cancelled;
            }

            var selectedIds = uidoc.Selection.GetElementIds();
            var selectedFixtures = FixtureSelectionService.GetSelectedLightingFixtures(doc, selectedIds);
            var selectedPowerSupplies = FixtureSelectionService.GetSelectedPowerSupplies(doc, selectedIds);
            var selectedKeypads = FixtureSelectionService.GetSelectedKeypads(doc, selectedIds);
            if (selectedFixtures.Count == 0 && selectedPowerSupplies.Count == 0 && selectedKeypads.Count == 0)
            {
                TaskDialog.Show("TurboTag", "No lighting fixtures, power supplies, or keypads selected.\nSelect at least one.");
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
                TagDirection linearChoice = PromptForDirectionLinear();
                if (linearChoice == TagDirection.None)
                {
                    return Result.Cancelled;
                }

                if (linearChoice == TagDirection.Combined || linearChoice == TagDirection.CombinedForced)
                {
                    bool forced = linearChoice == TagDirection.CombinedForced;
                    Result combinedResult = HandleCombinedLinear(doc, lineBasedFixtures, forced, ref totalTagged);
                    if (combinedResult != Result.Succeeded)
                        return combinedResult;
                }
                else
                {
                    string linearTypeName = linearChoice == TagDirection.Up ? "Tag_Top" : "Tag_Bottom";
                    FamilySymbol? linearTagType = TagTypeService.GetLinearTagType(doc, linearTypeName);
                    if (linearTagType == null)
                    {
                        TaskDialog.Show("TurboTag", $"Tag type '{linearTypeName}' in family '{TagConstants.LinearTagFamilyName}' not found.\nLoad this tag family into the project.");
                        return Result.Cancelled;
                    }

                    totalTagged += PlaceTags(doc, lineBasedFixtures, linearTagType, linearChoice, true);
                }
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

            if (selectedPowerSupplies.Count > 0)
            {
                FamilySymbol? switchIdTagType = TagTypeService.GetSwitchIdTagType(doc);
                if (switchIdTagType == null)
                {
                    TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.SwitchIdTagFamilyName}' not found.\nLoad this tag family into the project.");
                    return Result.Cancelled;
                }

                totalTagged += PlacePowerSupplyTags(doc, selectedPowerSupplies, switchIdTagType);
            }

            if (selectedKeypads.Count > 0)
            {
                FamilySymbol? keypadTagType = TagTypeService.GetKeypadTagType(doc);
                if (keypadTagType == null)
                {
                    TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.KeypadTagFamilyName}' not found.\nLoad this tag family into the project.");
                    return Result.Cancelled;
                }

                FamilySymbol? keypadTwoGangTagType = TagTypeService.GetKeypadTagType(doc, TagConstants.KeypadTwoGangTypeName);

                totalTagged += PlaceKeypadTags(doc, selectedKeypads, keypadTagType, keypadTwoGangTagType);
            }

            int totalSelected = selectedFixtures.Count + selectedPowerSupplies.Count + selectedKeypads.Count;
            if (totalSelected > 10)
            {
                TaskDialog.Show("TurboTag", $"Successfully tagged {totalTagged} of {totalSelected} fixtures.");
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

    private TagDirection PromptForDirectionLinear()
        => PromptForDirection(includeLeftRight: false, includeCombined: true);

    private TagDirection PromptForDirectionLinearUpDownOnly()
        => PromptForDirection(includeLeftRight: false, includeCombined: false);

    private TagDirection PromptForDirection()
        => PromptForDirection(includeLeftRight: true, includeCombined: false);

    private TagDirection PromptForDirection(bool includeLeftRight, bool includeCombined = false)
    {
        var dialog = new TagDirectionDialog(includeLeftRight, _revitHandle, includeCombined);
        return dialog.ShowDialog() == true ? dialog.SelectedDirection : TagDirection.None;
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

    private int PlacePowerSupplyTags(Document doc, List<FamilyInstance> powerSupplies, FamilySymbol tagType)
    {
        int successCount = 0;
        View activeView = doc.ActiveView;
        ElementId tagTypeId = tagType.Id;
        ElementId viewId = activeView.Id;
        string tagFamilyName = tagType.FamilyName;

        using (var trans = new Transaction(doc, "TurboTag - Place Power Supply Tags"))
        {
            var failureOptions = trans.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new TagFailurePreprocessor());
            trans.SetFailureHandlingOptions(failureOptions);

            trans.Start();

            foreach (FamilyInstance device in powerSupplies)
            {
                DeleteExistingTags(doc, device.Id, viewId, tagFamilyName);

                if (TryPlacePowerSupplyTag(doc, device, tagTypeId, viewId))
                {
                    successCount++;
                }
            }

            trans.Commit();
        }

        return successCount;
    }

    private bool TryPlacePowerSupplyTag(Document doc, FamilyInstance device, ElementId tagTypeId, ElementId viewId)
    {
        try
        {
            if (device.Location is not LocationPoint locPoint)
                return false;

            XYZ location = locPoint.Point;

            var reference = new Reference(device);
            IndependentTag? tag = IndependentTag.Create(
                doc, tagTypeId, viewId, reference,
                addLeader: false,
                TagOrientation.Horizontal,
                location);

            return tag != null;
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

    private int PlaceKeypadTags(Document doc, List<FamilyInstance> keypads, FamilySymbol tagType, FamilySymbol? twoGangTagType)
    {
        int successCount = 0;
        View activeView = doc.ActiveView;
        ElementId defaultTagTypeId = tagType.Id;
        ElementId twoGangTagTypeId = twoGangTagType?.Id ?? ElementId.InvalidElementId;
        ElementId viewId = activeView.Id;
        string tagFamilyName = tagType.FamilyName;

        using (var trans = new Transaction(doc, "TurboTag - Place Keypad Tags"))
        {
            var failureOptions = trans.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new TagFailurePreprocessor());
            trans.SetFailureHandlingOptions(failureOptions);

            trans.Start();

            foreach (FamilyInstance keypad in keypads)
            {
                DeleteExistingTags(doc, keypad.Id, viewId, tagFamilyName);

                bool isTwoGang = keypad.LookupParameter(TagConstants.KeypadTwoGangParamName)?.AsInteger() == 1;
                ElementId tagTypeId = isTwoGang && twoGangTagTypeId != ElementId.InvalidElementId
                    ? twoGangTagTypeId
                    : defaultTagTypeId;

                if (TryPlaceKeypadTag(doc, keypad, tagTypeId, viewId))
                {
                    successCount++;
                }
            }

            trans.Commit();
        }

        return successCount;
    }

    private bool TryPlaceKeypadTag(Document doc, FamilyInstance keypad, ElementId tagTypeId, ElementId viewId)
    {
        try
        {
            if (keypad.Location is not LocationPoint locPoint)
                return false;

            XYZ keypadLocation = locPoint.Point;

            var reference = new Reference(keypad);
            IndependentTag? tag = IndependentTag.Create(
                doc, tagTypeId, viewId, reference,
                addLeader: false,
                TagOrientation.Horizontal,
                keypadLocation);

            if (tag == null)
                return false;

            XYZ globalOffset;
            double angle;

            if (keypad.HostFace != null)
            {
                // Wall-hosted on linked model: derive offset from host face normal,
                // matching the TryPlaceTagFaceBased approach for wall sconces.
                XYZ offsetDirection = XYZ.Zero;
                Reference hostFaceRef = keypad.HostFace;
                Element? host = keypad.Host;

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

                if (offsetDirection.IsZeroLength())
                {
                    XYZ facing = keypad.FacingOrientation;
                    XYZ horizontal = new XYZ(facing.X, facing.Y, 0);
                    if (horizontal.GetLength() > 0.001)
                        offsetDirection = horizontal.Normalize();
                }

                globalOffset = !offsetDirection.IsZeroLength()
                    ? offsetDirection * TagConstants.KeypadOffsetFeet
                    : XYZ.Zero;

                // Rotate tag to align with the wall direction (perpendicular to offset)
                XYZ hand = -keypad.HandOrientation;
                angle = Math.Atan2(hand.Y, hand.X);
            }
            else
            {
                // Unhosted (2D): BasisX rotation works directly in the horizontal plane.
                XYZ localOffset = new XYZ(0, TagConstants.KeypadOffsetFeet, 0);
                globalOffset = TagPlacementService.TransformToGlobal(keypad, localOffset);

                Transform transform = keypad.GetTransform();
                angle = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
            }

            if (!globalOffset.IsZeroLength())
            {
                ElementTransformUtils.MoveElement(doc, tag.Id, globalOffset);
            }

            if (Math.Abs(angle) > 0.001)
            {
                XYZ tagPosition = keypadLocation + globalOffset;
                Line axis = Line.CreateBound(tagPosition, tagPosition + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, tag.Id, axis, angle);
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

    private static List<LinearRun> BuildForcedSingleRun(List<FamilyInstance> lineBasedFixtures)
    {
        if (lineBasedFixtures.Count == 0)
            return new List<LinearRun>();
        return new List<LinearRun> { new LinearRun(new List<FamilyInstance>(lineBasedFixtures)) };
    }

    private Result HandleCombinedLinear(Document doc, List<FamilyInstance> lineBasedFixtures, bool forced, ref int totalTagged)
    {
        List<LinearRun> runs = forced
            ? BuildForcedSingleRun(lineBasedFixtures)
            : LinearRunService.BuildRuns(lineBasedFixtures);
        var multiRuns = runs.Where(r => r.Members.Count > 1).ToList();
        var singleRuns = runs.Where(r => r.Members.Count == 1).ToList();

        // Pre-flight: combined tag family must have both Top and Bottom types loaded.
        FamilySymbol? combinedTop = TagTypeService.GetCombinedLinearTagType(doc, "Tag_Top");
        FamilySymbol? combinedBottom = TagTypeService.GetCombinedLinearTagType(doc, "Tag_Bottom");
        if (combinedTop == null || combinedBottom == null)
        {
            TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.CombinedLinearTagFamilyName}' (types 'Tag_Top' and 'Tag_Bottom') not found.\nLoad this tag family into the project.");
            return Result.Cancelled;
        }

        // Pre-flight: standard linear tag family must be loaded if any run-of-one will fall back.
        FamilySymbol? linearTop = null;
        FamilySymbol? linearBottom = null;
        if (singleRuns.Count > 0)
        {
            linearTop = TagTypeService.GetLinearTagType(doc, "Tag_Top");
            linearBottom = TagTypeService.GetLinearTagType(doc, "Tag_Bottom");
            if (linearTop == null || linearBottom == null)
            {
                TaskDialog.Show("TurboTag", $"Tag family '{TagConstants.LinearTagFamilyName}' (types 'Tag_Top' and 'Tag_Bottom') not found.\nLoad this tag family into the project (required for run-of-one fallback).");
                return Result.Cancelled;
            }
        }

        // Pre-flight: every fixture in every multi-fixture run must have a writable Run Length parameter.
        var missingParam = new List<string>();
        foreach (var run in multiRuns)
        {
            foreach (var member in run.Members)
            {
                Parameter? p = member.LookupParameter(TagConstants.RunLengthParamName);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double)
                {
                    string famName = member.Symbol?.Family?.Name ?? "(unknown family)";
                    string entry = $"{famName} (id {member.Id})";
                    if (!missingParam.Contains(entry))
                        missingParam.Add(entry);
                }
            }
        }

        if (missingParam.Count > 0)
        {
            string list = string.Join("\n  ", missingParam.Take(10));
            string suffix = missingParam.Count > 10 ? $"\n  …and {missingParam.Count - 10} more" : string.Empty;
            TaskDialog.Show("TurboTag",
                $"Combined tagging requires a writable Length instance parameter named '{TagConstants.RunLengthParamName}' on every fixture in a run.\nMissing or invalid on:\n  {list}{suffix}");
            return Result.Cancelled;
        }

        // Direction prompt for the combined run tag (and any run-of-one fallback tags).
        TagDirection direction = PromptForDirectionLinearUpDownOnly();
        if (direction == TagDirection.None)
            return Result.Cancelled;

        FamilySymbol combinedTagType = direction == TagDirection.Up ? combinedTop : combinedBottom;
        FamilySymbol? singleTagType = direction == TagDirection.Up ? linearTop : linearBottom;

        View activeView = doc.ActiveView;
        ElementId viewId = activeView.Id;

        using (var trans = new Transaction(doc, "TurboTag - Place Combined Tags"))
        {
            var failureOptions = trans.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new TagFailurePreprocessor());
            trans.SetFailureHandlingOptions(failureOptions);

            trans.Start();

            foreach (var run in multiRuns)
            {
                // Sum Linear Length across all members.
                double total = 0.0;
                foreach (var member in run.Members)
                {
                    Parameter? ll = member.LookupParameter("Linear Length");
                    if (ll != null && ll.StorageType == StorageType.Double)
                        total += ll.AsDouble();
                }

                // Write total to the lead, clear on every other member.
                foreach (var member in run.Members)
                {
                    Parameter rl = member.LookupParameter(TagConstants.RunLengthParamName);
                    rl.Set(member.Id == run.Lead.Id ? total : 0.0);
                }

                // Remove existing linear/combined tags from every member in this view.
                foreach (var member in run.Members)
                {
                    DeleteExistingTags(doc, member.Id, viewId, TagConstants.LinearTagFamilyName);
                    DeleteExistingTags(doc, member.Id, viewId, TagConstants.CombinedLinearTagFamilyName);
                }

                if (TryPlaceTag(doc, run.Lead, combinedTagType.Id, viewId, direction, isLineBased: true))
                    totalTagged++;
            }

            // Run-of-one falls back to the standard linear tag.
            if (singleRuns.Count > 0 && singleTagType != null)
            {
                foreach (var run in singleRuns)
                {
                    var fixture = run.Members[0];
                    DeleteExistingTags(doc, fixture.Id, viewId, TagConstants.LinearTagFamilyName);
                    DeleteExistingTags(doc, fixture.Id, viewId, TagConstants.CombinedLinearTagFamilyName);

                    if (TryPlaceTag(doc, fixture, singleTagType.Id, viewId, direction, isLineBased: true))
                        totalTagged++;
                }
            }

            trans.Commit();
        }

        return Result.Succeeded;
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
