using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Shared.Filters;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Wire.Constants;
using TurboSuite.Wire.Helpers;
using TurboSuite.Wire.Services;
using TurboSuite.Tag.Services;

namespace TurboSuite.Wire;

/// <summary>
/// TurboWire — creates electrical circuits and routes arc/spline wires between selected
/// lighting/electrical fixtures. Precondition: at least one WireType must exist in the project.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class WireCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;

        try
        {
            List<ElectricalSystem> preSelectedCircuits = GetPreSelectedElectricalCircuits(uiDoc);

            if (preSelectedCircuits.Count > 0)
            {
                using var txGroup = new TransactionGroup(doc, "TurboWire");
                txGroup.Start();

                foreach (ElectricalSystem circuit in preSelectedCircuits)
                {
                    // Wire every fixture on the circuit as one nearest-neighbor run,
                    // regardless of category — a mixed circuit routes through all its
                    // members rather than as separate per-category clusters.
                    List<FamilyInstance> fixturesOnCircuit = CircuitService.GetFixturesOnCircuit(circuit);
                    if (fixturesOnCircuit.Count >= 2)
                    {
                        Result result = WireMultipleFixtures(doc, fixturesOnCircuit, ref message);
                        if (result != Result.Succeeded)
                        {
                            txGroup.RollBack();
                            return result;
                        }
                    }
                }

                // Circuit-info dialog for every pre-selected circuit that was wired (switched
                // circuits are filtered out inside the service). Setting-gated.
                if (CircuitInfoService.PromptAndApply(doc, preSelectedCircuits, "TurboWire")
                    == CircuitInfoResult.Cancelled)
                {
                    txGroup.RollBack();
                    return Result.Cancelled;
                }

                txGroup.Assimilate();
                return Result.Succeeded;
            }

            List<FamilyInstance> preSelectedFixtures = GetPreSelectedFixtures(uiDoc);

            if (preSelectedFixtures.Count == 1)
            {
                return HandleSingleFixture(uiDoc, doc, preSelectedFixtures[0]);
            }

            if (preSelectedFixtures.Count >= 2)
            {
                return HandleMultipleFixtures(uiDoc, doc, preSelectedFixtures, ref message);
            }

            return ManualSelection(uiDoc, doc, ref message);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (
            ex.Message.IndexOf("electComponents", StringComparison.OrdinalIgnoreCase) >= 0
            || ex.Message.IndexOf("at least one component", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Revit rejects ElectricalSystem.Create when the selected fixtures can't
            // form one circuit — almost always a voltage mismatch (e.g. some at 120V,
            // some at 240V). Translate the cryptic "electComponents" error. Any open
            // transaction/group has already rolled back on the way out.
            TaskDialog.Show("TurboWire",
                "These fixtures can't be placed on one circuit — Revit rejected them as " +
                "electrically incompatible.\n\n" +
                "This is almost always a voltage mismatch (e.g. some fixtures at 120V and " +
                "others at 240V). Check that the selected fixtures share the same connector " +
                "voltage.");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static Result HandleSingleFixture(UIDocument uiDoc, Document doc, FamilyInstance fixture)
    {
        bool isSwitch = GeometryHelper.IsSwitch(fixture);
        var analysis = CircuitService.AnalyzeFixtures(new List<FamilyInstance> { fixture });

        if (analysis.SingleCircuit)
        {
            string existingComment = ParameterHelper.GetCircuitComments(analysis.SingleCircuitRef!);
            if (!string.IsNullOrEmpty(existingComment))
            {
                uiDoc.Selection.SetElementIds(new List<ElementId>());
                return Result.Succeeded;
            }
        }

        using var txGroup = new TransactionGroup(doc, "TurboWire");
        txGroup.Start();

        ElectricalSystem? circuit;

        if (analysis.SingleCircuit)
        {
            circuit = analysis.SingleCircuitRef!;
        }
        else
        {
            circuit = CircuitService.CreateCircuit(doc, new List<FamilyInstance> { fixture },
                assignPanel: !isSwitch);
            if (circuit == null)
            {
                txGroup.RollBack();
                return Result.Failed;
            }
        }

        if (isSwitch)
        {
            CircuitService.SetCircuitComments(doc, circuit, "switched");
            txGroup.Assimilate();
            return Result.Succeeded;
        }

        if (CircuitInfoService.PromptAndApply(doc, new[] { circuit }, "TurboWire")
            == CircuitInfoResult.Cancelled)
        {
            txGroup.RollBack();
            return Result.Cancelled;
        }

        txGroup.Assimilate();
        return Result.Succeeded;
    }

    private static Result HandleMultipleFixtures(UIDocument uiDoc, Document doc,
        List<FamilyInstance> fixtures, ref string message)
    {
        // Check for multiple circuits across entire selection — abort if found
        var fullAnalysis = CircuitService.AnalyzeFixtures(fixtures);
        if (fullAnalysis.MultipleCircuits)
        {
            TaskDialog.Show("TurboWire",
                $"Selected fixtures are on {fullAnalysis.CircuitMap.Count} different circuits.\n" +
                "Select fixtures from a single circuit.");
            return Result.Failed;
        }

        bool hasSwitch = fixtures.Any(f => GeometryHelper.IsSwitch(f));

        if (hasSwitch)
        {
            // Switch selections: one circuit for all fixtures (no category split),
            // no panel, "switched" comment, no dialog
            var analysis = CircuitService.AnalyzeFixtures(fixtures);
            ElectricalSystem? resultCircuit = null;

            if (analysis.AllUncircuited)
            {
                resultCircuit = CircuitService.CreateCircuit(doc, fixtures, assignPanel: false);
            }
            else if (analysis.SingleCircuit && analysis.UncircuitedFixtures.Count > 0)
            {
                CircuitService.AddFixturesToCircuit(doc, analysis.SingleCircuitRef!, analysis.UncircuitedFixtures);
                resultCircuit = analysis.SingleCircuitRef;
            }
            else if (analysis.SingleCircuit)
            {
                resultCircuit = analysis.SingleCircuitRef;
            }

            if (fixtures.Count >= 2)
            {
                Result result = WireMultipleFixtures(doc, fixtures, ref message);
                if (result != Result.Succeeded)
                    return result;
            }

            if (resultCircuit != null)
            {
                string existingComment = ParameterHelper.GetCircuitComments(resultCircuit);
                if (string.IsNullOrEmpty(existingComment))
                    CircuitService.SetCircuitComments(doc, resultCircuit, "switched");
            }

            return Result.Succeeded;
        }

        using var txGroup = new TransactionGroup(doc, "TurboWire");
        txGroup.Start();

        // One circuit for the whole selection, regardless of fixture category — the API
        // accepts mixed Lighting + Electrical members (verified), so a relay-switched
        // closet's downlights + switched receptacles land on a single circuit rather
        // than splitting. Wiring then runs a single nearest-neighbor path through every
        // fixture in the selection, not per-category clusters.
        var selectionAnalysis = CircuitService.AnalyzeFixtures(fixtures);

        ElectricalSystem? mixedCircuit = null;

        if (selectionAnalysis.AllUncircuited)
        {
            mixedCircuit = CircuitService.CreateCircuit(doc, fixtures);
        }
        else if (selectionAnalysis.SingleCircuit && selectionAnalysis.UncircuitedFixtures.Count > 0)
        {
            CircuitService.AddFixturesToCircuit(doc, selectionAnalysis.SingleCircuitRef!, selectionAnalysis.UncircuitedFixtures);
            mixedCircuit = selectionAnalysis.SingleCircuitRef;
        }
        else if (selectionAnalysis.SingleCircuit)
        {
            mixedCircuit = selectionAnalysis.SingleCircuitRef;
        }

        if (fixtures.Count >= 2)
        {
            Result result = WireMultipleFixtures(doc, fixtures, ref message);
            if (result != Result.Succeeded)
            {
                txGroup.RollBack();
                return result;
            }
        }

        // Circuit-info dialog for the wired circuit (created or joined). Setting-gated;
        // shows even when a comment already exists so room/panel can be corrected.
        if (mixedCircuit != null &&
            CircuitInfoService.PromptAndApply(doc, new[] { mixedCircuit }, "TurboWire")
                == CircuitInfoResult.Cancelled)
        {
            txGroup.RollBack();
            return Result.Cancelled;
        }

        txGroup.Assimilate();
        return Result.Succeeded;
    }

    private static Result ManualSelection(UIDocument uiDoc, Document doc, ref string message)
    {
        var filter = new FixtureSelectionFilter();

        Reference r1 = uiDoc.Selection.PickObject(
            ObjectType.Element, filter, "Select FIRST fixture");

        FamilyInstance? fixture1 = uiDoc.Document.GetElement(r1) as FamilyInstance;

        Reference r2 = uiDoc.Selection.PickObject(
            ObjectType.Element, filter, "Select SECOND fixture");

        FamilyInstance? fixture2 = uiDoc.Document.GetElement(r2) as FamilyInstance;

        return WireTwoFixtures(doc, fixture1!, fixture2!, useTagAwareArc: false, ref message, tagLookup: null);
    }

    private static List<ElectricalSystem> GetPreSelectedElectricalCircuits(UIDocument uiDoc)
    {
        Document doc = uiDoc.Document;
        ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();

        return selectedIds
            .Select(id => doc.GetElement(id))
            .Where(e => e is ElectricalSystem)
            .Cast<ElectricalSystem>()
            .ToList();
    }

    private static List<FamilyInstance> GetPreSelectedFixtures(UIDocument uiDoc)
    {
        Document doc = uiDoc.Document;
        ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();

        return selectedIds
            .Select(id => doc.GetElement(id))
            .Where(e => e is FamilyInstance fi &&
                        (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                         fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures))
            .Cast<FamilyInstance>()
            .ToList();
    }

    private static Result WireMultipleFixtures(Document doc, List<FamilyInstance> fixtures, ref string message)
    {
        List<FamilyInstance> orderedFixtures = FixtureOrderingService.OrderFixturesByProximity(fixtures);
        var tagLookup = ArcCalculator.BuildTagLookup(doc);

        // Compute centroid of all fixtures for outward-facing arc direction
        XYZ centroid = ComputeCentroid(orderedFixtures);

        for (int i = 0; i < orderedFixtures.Count - 1; i++)
        {
            FamilyInstance fixture1 = orderedFixtures[i];
            FamilyInstance fixture2 = orderedFixtures[i + 1];

            Result result = WireTwoFixtures(doc, fixture1, fixture2, useTagAwareArc: true, ref message, tagLookup, centroid);
            if (result != Result.Succeeded)
            {
                return result;
            }
        }

        return Result.Succeeded;
    }

    private static XYZ ComputeCentroid(List<FamilyInstance> fixtures)
    {
        double x = 0, y = 0, z = 0;
        int count = 0;
        foreach (FamilyInstance f in fixtures)
        {
            XYZ? loc = GeometryHelper.GetFixtureLocation(f);
            if (loc == null) continue;
            x += loc.X;
            y += loc.Y;
            z += loc.Z;
            count++;
        }
        return count > 0 ? new XYZ(x / count, y / count, z / count) : XYZ.Zero;
    }

    private static Result WireTwoFixtures(Document doc, FamilyInstance fixture1, FamilyInstance fixture2, bool useTagAwareArc, ref string message,
        Dictionary<ElementId, IndependentTag>? tagLookup = null, XYZ? groupCentroid = null)
    {
        Connector? c1 = GeometryHelper.GetElectricalConnector(fixture1, endTypeOnly: true);
        Connector? c2 = GeometryHelper.GetElectricalConnector(fixture2, endTypeOnly: true);

        if (c1 == null || c2 == null)
        {
            message = "Electrical connectors not found.";
            return Result.Failed;
        }

        if (c1.Origin.DistanceTo(c2.Origin) < WireConstants.MinDistanceTolerance)
        {
            message = "Fixtures are too close together.";
            return Result.Failed;
        }

        WireCreationService.DeleteWiresBetweenFixtures(doc, c1, c2);

        // Switch endpoint override: Revit visually snaps wire to family center.
        // 2D (unhosted): connector at 9" from origin, offset 0.01" via BasisX rotation.
        // 3D (wall-hosted): connector at origin, offset 9" away from wall via wall face normal.
        XYZ? switchOffset1 = WallSconceService.IsSwitch(fixture1)
            ? GetSwitchOffset(fixture1) : null;
        XYZ? switchOffset2 = WallSconceService.IsSwitch(fixture2)
            ? GetSwitchOffset(fixture2) : null;

        bool rps1 = ParameterHelper.HasRemotePowerSupply(fixture1);
        bool rps2 = ParameterHelper.HasRemotePowerSupply(fixture2);

        if (rps1 && rps2)
        {
            IList<XYZ> straightPoints = new List<XYZ> { c1.Origin, c2.Origin };
            return WireCreationService.CreateWire(doc, straightPoints, WiringType.Chamfer,
                c1, c2, null, null, 0, true, ref message, switchOffset1, switchOffset2);
        }

        if (rps1 != rps2)
        {
            TaskDialog.Show("TurboWire", "Power Supply Mismatch — The selected fixtures have different power supply configurations.");
        }

        bool isWallSconce = WallSconceService.IsWallSconce(fixture1) && WallSconceService.IsWallSconce(fixture2);
        bool isReceptacle = WallSconceService.IsReceptacle(fixture1) && WallSconceService.IsReceptacle(fixture2);
        bool isSplineCondition = isWallSconce || isReceptacle;
        IList<XYZ> wirePoints;
        WiringType wiringType;

        if (isSplineCondition)
        {
            XYZ wallNormal1 = GeometryHelper.GetWallFaceNormal(fixture1);
            XYZ wallNormal2 = GeometryHelper.GetWallFaceNormal(fixture2);

            double dotProduct = wallNormal1.DotProduct(wallNormal2);
            bool sameOrientation = Math.Abs(Math.Abs(dotProduct) - 1.0) < 0.001;

            if (sameOrientation)
            {
                double distance = c1.Origin.DistanceTo(c2.Origin);
                double familyScaleFactor = isWallSconce ? WallSconceService.GetFamilyScaleFactor(fixture1) : 1.0;
                bool facingSameDirection = dotProduct > 0;

                double connectorOffsetConst = isReceptacle
                    ? WireConstants.ReceptacleSplineConnectorOffset
                    : WireConstants.SplineConnectorOffset;
                double connectorOffset = connectorOffsetConst * familyScaleFactor;

                wirePoints = WallSconceService.CalculateWallSconceSplinePoints(
                    fixture1, fixture2,
                    c1.Origin, c2.Origin,
                    distance, familyScaleFactor,
                    facingSameDirection);
                wiringType = WiringType.Arc;
                return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2,
                    wallNormal1, wallNormal2,
                    connectorOffset, facingSameDirection, ref message, switchOffset1, switchOffset2);
            }
        }

        // Determine arc direction: tag → centroid → default
        int? tagDirection = useTagAwareArc
            ? ArcCalculator.GetArcDirectionFromTags(doc, fixture1, fixture2, c1.Origin, c2.Origin, tagLookup)
            : null;

        int arcDirection;
        if (tagDirection.HasValue)
        {
            arcDirection = tagDirection.Value;
        }
        else if (groupCentroid != null)
        {
            XYZ midpoint = (c1.Origin + c2.Origin) * 0.5;
            XYZ chordDir = (new XYZ(c2.Origin.X, c2.Origin.Y, 0) - new XYZ(c1.Origin.X, c1.Origin.Y, 0)).Normalize();
            XYZ perpDir = XYZ.BasisZ.CrossProduct(chordDir).Normalize();
            double dot = (new XYZ(groupCentroid.X, groupCentroid.Y, 0) - new XYZ(midpoint.X, midpoint.Y, 0)).DotProduct(perpDir);
            arcDirection = dot >= 0 ? -1 : 1;
        }
        else
        {
            arcDirection = 1;
        }

        // If fixtures share a non-axis-aligned rotation, evaluate off-axis in their local frame
        bool useLocalFrame = ArcCalculator.TryGetSharedRotation(fixture1, fixture2, out double sharedAngle);
        XYZ p1 = useLocalFrame ? ArcCalculator.RotateXY(c1.Origin, -sharedAngle) : c1.Origin;
        XYZ p2 = useLocalFrame ? ArcCalculator.RotateXY(c2.Origin, -sharedAngle) : c2.Origin;

        // Off-axis fixtures: corner arc (squared) or S-spline (elongated)
        if (ArcCalculator.IsOffAxis(p1, p2))
        {
            IList<XYZ> localPoints;
            if (ArcCalculator.IsSquared(p1, p2))
            {
                localPoints = ArcCalculator.CalculateCornerArcPoints(p1, p2, arcDirection);
            }
            else
            {
                localPoints = ArcCalculator.CalculateSSplinePoints(p1, p2);
            }

            wirePoints = useLocalFrame
                ? localPoints.Select(pt => ArcCalculator.RotateXY(pt, sharedAngle)).ToList()
                : localPoints;
            wiringType = WiringType.Arc;
            return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2, null, null, 0, true, ref message, switchOffset1, switchOffset2);
        }

        // On-axis: standard 24° arc
        wirePoints = ArcCalculator.CalculateArcWirePoints(c1.Origin, c2.Origin, WireConstants.ArcAngleDegrees, arcDirection);
        wiringType = WiringType.Arc;
        return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2, null, null, 0, true, ref message, switchOffset1, switchOffset2);
    }

    private static XYZ GetSwitchOffset(FamilyInstance fixture)
    {
        if (fixture.HostFace != null)
        {
            // 3D wall-hosted: connector at origin, offset 9" away from wall
            XYZ wallNormal = GeometryHelper.GetWallFaceNormal(fixture);
            return wallNormal * (9.0 / 12.0);
        }

        // 2D unhosted: connector at 9" from origin, offset 0.01" along local +Y
        return TagPlacementService.TransformToGlobal(fixture, new XYZ(0, 0.01 / 12.0, 0));
    }
}
