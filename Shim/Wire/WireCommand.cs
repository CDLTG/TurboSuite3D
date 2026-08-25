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
using TurboSuite.Zones.Services;

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
                // circuits are filtered out inside the service). Setting-gated. When the whole
                // batch is shade circuits, the picker offers shade (35 V) locations instead.
                bool shadeCircuits = preSelectedCircuits.All(ShadeDemandProvider.IsShadeCircuit);
                if (CircuitInfoService.PromptAndApply(doc, preSelectedCircuits, "TurboWire", shadeCircuits)
                    == CircuitInfoResult.Cancelled)
                {
                    txGroup.RollBack();
                    return Result.Cancelled;
                }

                txGroup.Assimilate();
                return Result.Succeeded;
            }

            List<FamilyInstance> preSelectedFixtures = GetPreSelectedFixtures(uiDoc);

            // Shade mode: a shade motor is circuited one-shade-per-circuit onto a shade (35 V)
            // location. It fires only for a single shade with nothing else selected — a shade
            // mixed with any other fixture, or multiple shades, is rejected (shades wire one at
            // a time, so each keeps its own comment/circuit).
            int shadeCount = preSelectedFixtures.Count(ShadeDemandProvider.IsShadeMotor);
            if (shadeCount > 0)
            {
                if (preSelectedFixtures.Count != 1)
                {
                    TaskDialog.Show("TurboWire",
                        "Shade motors are wired one at a time. Select a single shade — not " +
                        "several, and not mixed with other fixtures.");
                    return Result.Cancelled;
                }
                return HandleSingleShade(uiDoc, doc, preSelectedFixtures[0]);
            }

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

    /// <summary>
    /// Shade mode: one shade → one circuit on a shade (35 V) location. Mirrors
    /// <see cref="HandleSingleFixture"/> minus the switch and wire-routing branches (a lone shade
    /// has nothing to route to), and defaults/filters the panel picker to shade locations. The
    /// circuit-info dialog is otherwise identical — Comment, "Zone", and Room Override (the last
    /// captured for a future Lutron export, though nothing in TurboSuite reads it for shades yet).
    /// </summary>
    private static Result HandleSingleShade(UIDocument uiDoc, Document doc, FamilyInstance shade)
    {
        var analysis = CircuitService.AnalyzeFixtures(new List<FamilyInstance> { shade });

        // Already circuited with a comment → nothing to do, same as the lighting single path.
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
            circuit = CircuitService.CreateCircuit(doc, new List<FamilyInstance> { shade },
                assignPanel: true, shadePanels: true);
            if (circuit == null)
            {
                txGroup.RollBack();
                return Result.Failed;
            }
        }

        if (CircuitInfoService.PromptAndApply(doc, new[] { circuit }, "TurboWire", shadePanels: true)
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
            // Normals from each fixture's own transform (mirror-corrected), not the host-face
            // reference — so casework/door-hosted sconces/receptacles no longer collapse to the
            // constant BasisY fallback, which broke both the sameOrientation gate (spline skipped)
            // and the connector-offset direction (stubs along the wall instead of out of it).
            // See GeometryHelper.GetWallFaceNormalFromTransform.
            XYZ wallNormal1 = GeometryHelper.GetWallFaceNormalFromTransform(fixture1);
            XYZ wallNormal2 = GeometryHelper.GetWallFaceNormalFromTransform(fixture2);

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

        // Linear end-to-end routing (Screenshot_518/526): relocate each fixture's effective wiring
        // point from its connector to a chosen END when the fixture is linear, then let the SAME
        // routing decision below (off-axis corner/S-spline, else on-axis arc) draw the wire between
        // those points — so an end-to-end run picks its own shape instead of a forced arc. A
        // non-linear fixture (square downlight) keeps its connector as the routing point; switches
        // stay deferred, keeping their existing center-routed, nudged-vertex behavior. The real
        // connectors are never touched — the chosen ends ride the endOffset -> SetVertex hook.
        View view = doc.ActiveView;
        bool lin1 = TryGetLongAxis(fixture1, view, out XYZ longDir1, out double half1) && switchOffset1 == null;
        bool lin2 = TryGetLongAxis(fixture2, view, out XYZ longDir2, out double half2) && switchOffset2 == null;

        XYZ r1 = c1.Origin, r2 = c2.Origin;
        if (lin1 || lin2)
        {
            // Bulge = perpendicular to the connector chord, on the side the arc leans — the tiebreak
            // when a fixture's two ends are equidistant (the symmetric side-by-side case).
            XYZ chordDir = new XYZ(c2.Origin.X - c1.Origin.X, c2.Origin.Y - c1.Origin.Y, 0).Normalize();
            XYZ bulge = XYZ.BasisZ.CrossProduct(chordDir).Normalize() * arcDirection;
            (r1, r2) = ChooseEndpoints(
                c1.Origin, lin1 ? longDir1 : null, half1,
                c2.Origin, lin2 ? longDir2 : null, half2, bulge);
            // Inline fixtures whose chosen ends collapse together → route the connectors instead.
            if (r1.DistanceTo(r2) < MinEndGap) { r1 = c1.Origin; r2 = c2.Origin; }
        }

        // Each endpoint's vertex offset: the shift out to a relocated linear end, otherwise the switch
        // nudge (null for a plain fixture). A zero shift falls back to the switch offset / null.
        XYZ sh1 = r1 - c1.Origin;
        XYZ sh2 = r2 - c2.Origin;
        XYZ? endOff1 = sh1.IsZeroLength() ? switchOffset1 : sh1;
        XYZ? endOff2 = sh2.IsZeroLength() ? switchOffset2 : sh2;

        // If fixtures share a non-axis-aligned rotation, evaluate off-axis in their local frame
        bool useLocalFrame = ArcCalculator.TryGetSharedRotation(fixture1, fixture2, out double sharedAngle);
        XYZ p1 = useLocalFrame ? ArcCalculator.RotateXY(r1, -sharedAngle) : r1;
        XYZ p2 = useLocalFrame ? ArcCalculator.RotateXY(r2, -sharedAngle) : r2;

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
            // Terminals stay at the connectors in the Create input — Revit pins a wire's ends to its
            // connectors, and handing it the relocated ends conflicts with that (it inserts a
            // connector vertex, so the end nudge then collides and the end snaps back to center).
            // The interior points already carry the shape; endOff1/endOff2 push the terminals out.
            wirePoints[0] = c1.Origin;
            wirePoints[wirePoints.Count - 1] = c2.Origin;
            wiringType = WiringType.Arc;
            return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2, null, null, 0, true, ref message, endOff1, endOff2);
        }

        // On-axis: standard 24° arc, shaped by the chosen routing points (the apex rides between/above
        // them). Terminals stay at the connectors in the Create input; endOff1/endOff2 push them out
        // to the ends afterward — see the note in the off-axis branch above.
        wirePoints = ArcCalculator.CalculateArcWirePoints(r1, r2, WireConstants.ArcAngleDegrees, arcDirection);
        wirePoints[0] = c1.Origin;
        wirePoints[wirePoints.Count - 1] = c2.Origin;
        wiringType = WiringType.Arc;
        return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2, null, null, 0, true, ref message, endOff1, endOff2);
    }

    // Linear end-to-end gate tuning. A fixture counts as "linear" when its long extent is at least
    // this many times its short extent (a light bar is ~24x; a 2x4 troffer ~2x stays center-wired).
    private const double LinearRatioThreshold = 3.0;
    // Two end-pairs whose lengths are within this of the shortest are treated as tied, and the bulge
    // (tag/centroid intent) breaks the tie. This is what decides top-vs-bottom in the symmetric
    // side-by-side case, where the two candidate pairs are exactly equal.
    private const double EndTieEpsilon = 0.5; // ft
    // Below this, the two chosen ends are treated as coincident (inline fixtures that meet/overlap)
    // and the pair routes between the connectors instead, rather than draw a degenerate stub.
    private const double MinEndGap = 0.25; // ft (3")

    /// <summary>
    /// Picks each fixture's routing point: for a linear fixture, one of its two ends; for a non-linear
    /// one (<paramref name="longDir1"/>/<paramref name="longDir2"/> null), its connector. Chooses the
    /// end-pair that minimizes the distance between the two points (the nearest ends — what a drafter
    /// wires by hand), breaking a tie toward the <paramref name="bulge"/> side so the symmetric
    /// side-by-side case follows the tag/centroid intent. The chosen points are then handed to the
    /// existing off-axis/on-axis routing, which decides the wire's shape.
    /// </summary>
    private static (XYZ r1, XYZ r2) ChooseEndpoints(
        XYZ c1, XYZ? longDir1, double half1,
        XYZ c2, XYZ? longDir2, double half2, XYZ bulge)
    {
        IReadOnlyList<XYZ> cand1 = EndCandidates(c1, longDir1, half1);
        IReadOnlyList<XYZ> cand2 = EndCandidates(c2, longDir2, half2);

        double bestDist = double.MaxValue;
        foreach (XYZ p in cand1)
            foreach (XYZ q in cand2)
                bestDist = Math.Min(bestDist, p.DistanceTo(q));

        // Among the closest pairs (within the tie window), prefer the one whose ends sit farthest
        // along the bulge — a no-op when there is a single clear nearest pair.
        double bestScore = double.NegativeInfinity;
        XYZ r1 = c1, r2 = c2;
        foreach (XYZ p in cand1)
            foreach (XYZ q in cand2)
            {
                if (p.DistanceTo(q) > bestDist + EndTieEpsilon) continue;
                double score = (p - c1).DotProduct(bulge) + (q - c2).DotProduct(bulge);
                if (score > bestScore) { bestScore = score; r1 = p; r2 = q; }
            }
        return (r1, r2);
    }

    /// <summary>
    /// A fixture's candidate routing points: its two long-axis ends when linear, otherwise just its
    /// connector origin.
    /// </summary>
    private static IReadOnlyList<XYZ> EndCandidates(XYZ c, XYZ? longDir, double half)
    {
        if (longDir == null) return new[] { c };
        return new[] { c + longDir * half, c - longDir * half };
    }

    /// <summary>
    /// Resolves a fixture's long-axis unit direction (from the BasisX angle, per CLAUDE.md) and its
    /// half-length. Returns false when the fixture's extents can't be measured or it isn't clearly
    /// linear (long/short ratio below <see cref="LinearRatioThreshold"/>).
    /// </summary>
    private static bool TryGetLongAxis(FamilyInstance f, View view, out XYZ longDir, out double halfLen)
    {
        longDir = XYZ.BasisX;
        halfLen = 0;

        // GetSymbolExtents: length = local-Y extent, width = local-X extent.
        (double length, double width) = GeometryHelper.GetSymbolExtents(f, view, 0);
        if (length <= 0 || width <= 0) return false;

        double maxExt = Math.Max(length, width);
        double minExt = Math.Min(length, width);
        if (maxExt / minExt < LinearRatioThreshold) return false; // square-ish → not linear

        double angle = GeometryHelper.GetTransformAngle(f.GetTransform());
        XYZ localX = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
        XYZ localY = new XYZ(-Math.Sin(angle), Math.Cos(angle), 0);
        longDir = width >= length ? localX : localY; // long axis is whichever extent is larger
        halfLen = maxExt / 2.0;
        return true;
    }

    private static XYZ GetSwitchOffset(FamilyInstance fixture)
    {
        if (fixture.HostFace != null)
        {
            // 3D wall-hosted: connector at origin, offset 9" away from wall. Normal derived from the
            // fixture's own transform (mirror-corrected), not the host-face reference — so casework/
            // door-hosted switches nudge the right way instead of collapsing to the wall. See
            // GeometryHelper.GetWallFaceNormalFromTransform.
            XYZ wallNormal = GeometryHelper.GetWallFaceNormalFromTransform(fixture);
            return wallNormal * (9.0 / 12.0);
        }

        // 2D unhosted: connector at 9" from origin, offset 0.01" along local +Y
        return TagPlacementService.TransformToGlobal(fixture, new XYZ(0, 0.01 / 12.0, 0));
    }
}
