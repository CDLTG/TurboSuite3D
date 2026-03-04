using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Filters;
using TurboSuite.Bubble.Placement;
using TurboSuite.Bubble.Services;
using TurboSuite.Shared.Filters;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble;

/// <summary>
/// Creates a switchleg tag and wire connection for lighting fixtures and electrical fixtures.
/// Works in floor plan and ceiling plan views only.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class BubbleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        var doc = uidoc.Document;
        var activeView = doc.ActiveView;

        try
        {
            if (!IsValidViewType(activeView))
                return Result.Cancelled;

            var wireTypeId = FixtureAnalysisService.FindFirstWireType(doc);
            if (wireTypeId == null)
            {
                ShowError("No wire types available in the project.");
                return Result.Failed;
            }

            var selectedElement = PromptForSelection(uidoc, doc);
            if (selectedElement == null)
                return Result.Cancelled;

            if (selectedElement is IndependentTag selectedTag)
                return ExecuteLightingFixturePath(doc, uidoc, activeView, selectedTag, wireTypeId);

            if (selectedElement is FamilyInstance electricalFixture &&
                electricalFixture.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures)
                return ExecuteElectricalFixturePath(doc, uidoc, activeView, electricalFixture, wireTypeId);

            ShowError("Selected element is not a lighting fixture tag or electrical fixture.");
            return Result.Failed;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            ShowError($"Unexpected error: {ex.Message}");
            return Result.Failed;
        }
    }

    #region Lighting Fixture Path

    private static Result ExecuteLightingFixturePath(
        Document doc, UIDocument uidoc, View activeView,
        IndependentTag selectedTag, ElementId wireTypeId)
    {
        var fixture = GetTaggedFixture(doc, selectedTag);
        if (fixture == null)
        {
            ShowError("Tagged element is not a lighting fixture.");
            return Result.Failed;
        }

        var fixtureConnector = GeometryHelper.GetElectricalConnector(fixture);
        if (fixtureConnector == null)
        {
            ShowError("Lighting fixture has no electrical connector.");
            return Result.Failed;
        }

        var isLineBased = GeometryHelper.IsLineBasedFixture(fixture);
        var isWallSconce = !isLineBased && GeometryHelper.IsWallSconce(fixture);
        var isVerticalFace = !isLineBased && (GeometryHelper.IsOnVerticalFace(fixture) || isWallSconce);
        var hasRemotePowerSupply = ParameterHelper.HasRemotePowerSupply(fixture);

        IPlacementCalculator placement;
        if (isLineBased)
            placement = new LineBasedPlacementCalculator(doc, activeView, fixture, selectedTag);
        else if (isVerticalFace)
            placement = new VerticalFacePlacementCalculator(doc, activeView, fixture, selectedTag);
        else
            placement = new HorizontalPlacementCalculator(doc, activeView, fixture, selectedTag, hasRemotePowerSupply);

        var flipPoint = PromptForFlipPoint(uidoc, doc, placement.FixturePoint);
        placement.CalculateFinalPositions(flipPoint);

        ElementId? tagTypeId;
        if (hasRemotePowerSupply)
        {
            bool effectiveFlip;
            if (isLineBased)
            {
                var lineBasedPlacement = (LineBasedPlacementCalculator)placement;
                effectiveFlip = lineBasedPlacement.IsLineDirectionReversed
                    ? !placement.IsFlipped
                    : placement.IsFlipped;
            }
            else if (isVerticalFace)
            {
                effectiveFlip = placement.IsFlipped;
            }
            else
            {
                var horizontalCalc = (HorizontalPlacementCalculator)placement;
                effectiveFlip = horizontalCalc.DetermineEffectiveFlipForRPS();
            }

            var typeName = effectiveFlip ? BubbleConstants.RemoteSwitchlegTypeRight : BubbleConstants.RemoteSwitchlegTypeLeft;
            tagTypeId = FixtureAnalysisService.FindTagType(doc, BubbleConstants.RemoteSwitchlegTagFamily, typeName);
            if (tagTypeId == null)
            {
                ShowError($"Load tag type '{BubbleConstants.RemoteSwitchlegTagFamily}' - '{typeName}' before using TurboBubble.");
                return Result.Cancelled;
            }
        }
        else
        {
            tagTypeId = FixtureAnalysisService.FindTagType(doc, BubbleConstants.SwitchlegTagFamily);
            if (tagTypeId == null)
            {
                ShowError($"Load tag '{BubbleConstants.SwitchlegTagFamily}' before using TurboBubble.");
                return Result.Cancelled;
            }
        }

        XYZ? wallNormal = null;
        if (isWallSconce)
        {
            wallNormal = GeometryHelper.GetWallFaceNormal(fixture);
        }

        CreateTagAndWire(doc, activeView, fixture, selectedTag, placement, tagTypeId, wireTypeId, fixtureConnector, isLineBased, isWallSconce, wallNormal);

        return Result.Succeeded;
    }

    #endregion

    #region Electrical Fixture Path

    private static Result ExecuteElectricalFixturePath(
        Document doc, UIDocument uidoc, View activeView,
        FamilyInstance fixture, ElementId wireTypeId)
    {
        var fixtureConnector = GeometryHelper.GetElectricalConnector(fixture);
        if (fixtureConnector == null)
        {
            ShowError("Electrical fixture has no electrical connector.");
            return Result.Failed;
        }

        var tagTypeId = FixtureAnalysisService.FindTagType(
            doc, BuiltInCategory.OST_ElectricalFixtureTags,
            BubbleConstants.ElectricalSwitchlegTagFamily);
        if (tagTypeId == null)
        {
            ShowError($"Load tag '{BubbleConstants.ElectricalSwitchlegTagFamily}' before using TurboBubble.");
            return Result.Cancelled;
        }

        var fixtureOrigin = ((LocationPoint)fixture.Location).Point;

        var rotation = GetElectricalFixtureRotation(fixture);
        var cosR = Math.Cos(rotation);
        var sinR = Math.Sin(rotation);
        var localX = new XYZ(cosR, sinR, 0);
        var localY = new XYZ(-sinR, cosR, 0);

        var isVerticalPlacement = IsElectricalVerticalFamily(fixture);

        // Annotation midpoint is 6" from origin in the fixture's local Y direction
        var fixtureMidpoint = fixtureOrigin + localY * BubbleConstants.ElectricalMidpointOffsetFt;

        // Prompt user to pick flip direction
        var flipPoint = PromptForFlipPoint(uidoc, doc, fixtureOrigin);

        // Determine direction using fixture's local coordinate system
        var globalToLocal = Transform.CreateRotationAtPoint(XYZ.BasisZ, -rotation, fixtureOrigin);
        var flipLocal = globalToLocal.OfPoint(flipPoint);
        var fixtureLocal = globalToLocal.OfPoint(fixtureOrigin);

        XYZ tagPosition;
        List<XYZ> wireVertices;
        double direction;

        if (isVerticalPlacement)
        {
            // Vertical families: flip over X axis (up/down), +/-10" along localY only
            direction = flipLocal.Y >= fixtureLocal.Y ? 1.0 : -1.0;

            tagPosition = fixtureOrigin + localY * (direction * BubbleConstants.ElectricalVerticalTagOffsetFt);

            // Generate arc vertices to approximate circular wire
            // Arc defined by center, radius, start point (4",0"), and sweep angle
            // Start angle computed from center to start point
            var arcCenterX = BubbleConstants.ElectricalVerticalArcCenterXFt;
            var arcCenterY = BubbleConstants.ElectricalVerticalArcCenterYFt;
            var arcRadius = BubbleConstants.ElectricalVerticalArcRadiusFt;
            var startX = 4.0 * BubbleConstants.InchesToFeet; // start at (4", 0")
            var startY = 0.0;
            var startAngle = Math.Atan2(startY - arcCenterY, startX - arcCenterX);
            var sweepRad = BubbleConstants.ElectricalVerticalArcSweepDeg * Math.PI / 180.0;
            var segments = BubbleConstants.ElectricalVerticalArcSegments;

            wireVertices = new List<XYZ>(segments - 1);
            // Generate intermediate vertices (skip first = connector, skip last = end)
            for (int i = 1; i < segments; i++)
            {
                var angle = startAngle + sweepRad * i / segments;
                var px = arcCenterX + arcRadius * Math.Cos(angle);
                var py = arcCenterY + arcRadius * Math.Sin(angle);
                wireVertices.Add(fixtureOrigin + localX * px + localY * (direction * py));
            }
            // Add arc endpoint
            var endAngle = startAngle + sweepRad;
            var endX = arcCenterX + arcRadius * Math.Cos(endAngle);
            var endY = arcCenterY + arcRadius * Math.Sin(endAngle);
            wireVertices.Add(fixtureOrigin + localX * endX + localY * (direction * endY));
        }
        else
        {
            // Default: flip over Y axis (left/right), offset along localX
            direction = flipLocal.X >= fixtureLocal.X ? 1.0 : -1.0;

            tagPosition = fixtureMidpoint + localX * (direction * BubbleConstants.ElectricalTagOffsetFt);

            var v2 = fixtureOrigin
                + localX * (direction * BubbleConstants.ElectricalV2XOffsetFt)
                + localY * BubbleConstants.ElectricalV2YOffsetFt;

            var v3 = fixtureOrigin
                + localX * (direction * BubbleConstants.ElectricalV3XOffsetFt)
                + localY * BubbleConstants.ElectricalV3YOffsetFt;

            wireVertices = new List<XYZ>(2) { v2, v3 };
        }

        using (var trans = new Transaction(doc, "TurboBubble"))
        {
            trans.Start();

            DeleteExistingElectricalSwitchlegTags(doc, fixture);
            var wiresDeleted = DeleteUnconnectedWires(doc, fixtureConnector);
            if (wiresDeleted)
                doc.Regenerate();

            // Create tag
            var newTag = IndependentTag.Create(
                doc, activeView.Id, new Reference(fixture), false,
                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                tagPosition);
            newTag.ChangeTypeId(tagTypeId);
            newTag.TagHeadPosition = tagPosition;

            // Rotate tag to match fixture orientation
            if (Math.Abs(rotation) > BubbleConstants.RotationEpsilon)
            {
                var axis = Line.CreateBound(tagPosition, tagPosition + XYZ.BasisZ * 10);
                ElementTransformUtils.RotateElement(doc, newTag.Id, axis, rotation);
            }

            // Create wire
            var wire = ElectricalWire.Create(doc, wireTypeId, activeView.Id, WiringType.Arc, wireVertices, fixtureConnector, null);

            // SetVertex to offset wire start from connector
            var connOrigin = fixtureConnector.Origin;
            if (isVerticalPlacement)
            {
                wire.SetVertex(0, connOrigin + localX * (4.0 * BubbleConstants.InchesToFeet));
            }
            else
            {
                wire.SetVertex(0, connOrigin + localY * BubbleConstants.WireOffsetEndInitialFt);
                wire.SetVertex(0, connOrigin + localY * BubbleConstants.ElectricalWireStartOffsetFt);
            }

            trans.Commit();
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// Checks if the electrical fixture belongs to a family that uses vertical (up/down) switchleg placement.
    /// </summary>
    private static bool IsElectricalVerticalFamily(FamilyInstance fixture)
    {
        var familyName = fixture.Symbol?.FamilyName;
        return familyName != null &&
               BubbleConstants.ElectricalVerticalFamilies.Contains(familyName);
    }

    /// <summary>
    /// Gets the rotation angle of an electrical fixture using BasisX.
    /// Same approach as PlacementCalculatorBase.GetFixtureRotation.
    /// </summary>
    private static double GetElectricalFixtureRotation(FamilyInstance fixture)
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
                        var rot = Math.Atan2(xAxis.Y, xAxis.X);
                        if (Math.Abs(rot) > BubbleConstants.RotationEpsilon)
                            return rot;
                        break;
                    }
                }
            }
        }

        return ((LocationPoint)fixture.Location)?.Rotation ?? 0.0;
    }

    private static void DeleteExistingElectricalSwitchlegTags(Document doc, FamilyInstance fixture)
    {
        var tagsToDelete = new List<ElementId>();

        using (var collector = new FilteredElementCollector(doc))
        {
            foreach (var elem in collector
                .OfCategory(BuiltInCategory.OST_ElectricalFixtureTags)
                .OfClass(typeof(IndependentTag)))
            {
                if (elem is not IndependentTag tag)
                    continue;

                if (!tag.GetTaggedLocalElementIds().Contains(fixture.Id))
                    continue;

                var tagSymbol = doc.GetElement(tag.GetTypeId()) as FamilySymbol;
                if (tagSymbol?.FamilyName == BubbleConstants.ElectricalSwitchlegTagFamily)
                    tagsToDelete.Add(tag.Id);
            }
        }

        if (tagsToDelete.Count > 0)
            doc.Delete(tagsToDelete);
    }

    #endregion

    #region Validation

    private static bool IsValidViewType(View view)
    {
        return view.ViewType == ViewType.FloorPlan ||
               view.ViewType == ViewType.CeilingPlan;
    }

    #endregion

    #region User Interaction

    private static Element? PromptForSelection(UIDocument uidoc, Document doc)
    {
        var selRef = uidoc.Selection.PickObject(
            ObjectType.Element,
            new BubbleSelectionFilter(),
            "Select a lighting fixture tag or electrical fixture");

        return doc.GetElement(selRef);
    }

    private static FamilyInstance? GetTaggedFixture(Document doc, IndependentTag tag)
    {
        foreach (var id in tag.GetTaggedLocalElementIds())
        {
            if (doc.GetElement(id) is FamilyInstance fixture)
                return fixture;
        }
        return null;
    }

    private static XYZ PromptForFlipPoint(UIDocument uidoc, Document doc, XYZ origin)
    {
        var activeView = uidoc.ActiveView;
        var originalSketchPlane = activeView.SketchPlane;
        var tempSketchPlaneId = ElementId.InvalidElementId;

        try
        {
            using (var trans = new Transaction(doc, "Create Temp Sketch Plane"))
            {
                trans.Start();
                var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, origin);
                var tempPlane = SketchPlane.Create(doc, plane);
                tempSketchPlaneId = tempPlane.Id;
                activeView.SketchPlane = tempPlane;
                trans.Commit();
            }

            return uidoc.Selection.PickPoint("Pick flip direction point");
        }
        finally
        {
            using (var trans = new Transaction(doc, "Cleanup Sketch Plane"))
            {
                trans.Start();
                if (originalSketchPlane != null)
                    activeView.SketchPlane = originalSketchPlane;
                if (tempSketchPlaneId != ElementId.InvalidElementId)
                    doc.Delete(tempSketchPlaneId);
                trans.Commit();
            }
        }
    }

    #endregion

    #region Element Creation

    private static void CreateTagAndWire(
        Document doc,
        View view,
        FamilyInstance fixture,
        IndependentTag sourceTag,
        IPlacementCalculator placement,
        ElementId tagTypeId,
        ElementId wireTypeId,
        Connector fixtureConnector,
        bool isLineBased = false,
        bool isWallSconce = false,
        XYZ? wallNormal = null)
    {
        using (var trans = new Transaction(doc, "TurboBubble"))
        {
            trans.Start();

            DeleteExistingSwitchlegTags(doc, fixture, sourceTag);
            var wiresDeleted = DeleteUnconnectedWires(doc, fixtureConnector);

            if (wiresDeleted)
                doc.Regenerate();

            var newTag = TagPlacementService.CreateAndConfigureTag(doc, view, fixture, sourceTag, placement, tagTypeId);

            if (isLineBased)
                WirePlacementService.CreateWireWithOffsetEnd(doc, view, placement, wireTypeId, fixtureConnector);
            else if (isWallSconce && wallNormal != null)
                WirePlacementService.CreateWireWithWallSconceOffset(doc, view, placement, wireTypeId, fixtureConnector, wallNormal, fixture);
            else
                WirePlacementService.CreateWire(doc, view, placement, wireTypeId, fixtureConnector);

            trans.Commit();
        }
    }

    private static void DeleteExistingSwitchlegTags(Document doc, FamilyInstance fixture, IndependentTag sourceTag)
    {
        var tagsToDelete = new List<ElementId>();

        using (var collector = new FilteredElementCollector(doc))
        {
            foreach (var elem in collector
                .OfCategory(BuiltInCategory.OST_LightingFixtureTags)
                .OfClass(typeof(IndependentTag)))
            {
                if (elem is not IndependentTag tag || tag.Id == sourceTag.Id)
                    continue;

                var taggedIds = tag.GetTaggedLocalElementIds();
                if (!taggedIds.Contains(fixture.Id))
                    continue;

                var tagSymbol = doc.GetElement(tag.GetTypeId()) as FamilySymbol;
                if (tagSymbol?.FamilyName == BubbleConstants.SwitchlegTagFamily ||
                    tagSymbol?.FamilyName == BubbleConstants.RemoteSwitchlegTagFamily)
                {
                    tagsToDelete.Add(tag.Id);
                }
            }
        }

        if (tagsToDelete.Count > 0)
            doc.Delete(tagsToDelete);
    }

    private static bool DeleteUnconnectedWires(Document doc, Connector fixtureConnector)
    {
        var wiresToDelete = new List<ElementId>();

        var connectedRefs = fixtureConnector.AllRefs;
        if (connectedRefs == null)
            return false;

        foreach (Connector connectedConn in connectedRefs)
        {
            var owner = connectedConn.Owner;
            if (owner is not ElectricalWire wire)
                continue;

            if (IsWireOnlyConnectedToFixture(wire, fixtureConnector))
            {
                wiresToDelete.Add(wire.Id);
            }
        }

        if (wiresToDelete.Count > 0)
        {
            doc.Delete(wiresToDelete);
            return true;
        }

        return false;
    }

    private static bool IsWireOnlyConnectedToFixture(ElectricalWire wire, Connector fixtureConnector)
    {
        var wireConnectorManager = wire.ConnectorManager;
        if (wireConnectorManager == null)
            return true;

        foreach (Connector wireConn in wireConnectorManager.Connectors)
        {
            var refs = wireConn.AllRefs;
            if (refs == null)
                continue;

            foreach (Connector connectedTo in refs)
            {
                if (connectedTo.Owner.Id == fixtureConnector.Owner.Id)
                    continue;

                if (connectedTo.Owner is not ElectricalWire)
                    return false;
            }
        }

        return true;
    }

    #endregion

    private static void ShowError(string message)
    {
        TaskDialog.Show("TurboBubble Error", message);
    }
}
