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
using TurboSuite.Wire.Views;

namespace TurboSuite.Wire;

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
                foreach (ElectricalSystem circuit in preSelectedCircuits)
                {
                    List<FamilyInstance> fixturesOnCircuit = GetFixturesOnCircuit(circuit);
                    foreach (var group in fixturesOnCircuit.GroupBy(f => f.Category.BuiltInCategory))
                    {
                        List<FamilyInstance> groupList = group.ToList();
                        if (groupList.Count >= 2)
                        {
                            Result result = WireMultipleFixtures(doc, groupList, ref message);
                            if (result != Result.Succeeded)
                                return result;
                        }
                    }
                }

                // Show comments dialog for pre-selected circuits that have no comment
                var circuitsToComment = preSelectedCircuits
                    .Where(c => string.IsNullOrEmpty(ParameterHelper.GetCircuitComments(c)))
                    .ToList();
                if (circuitsToComment.Count > 0)
                    ShowCommentsDialogAndApply(doc, circuitsToComment);

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
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static Result HandleSingleFixture(UIDocument uiDoc, Document doc, FamilyInstance fixture)
    {
        var analysis = CircuitService.AnalyzeFixtures(new List<FamilyInstance> { fixture });

        ElectricalSystem? circuit;

        if (analysis.SingleCircuit)
        {
            circuit = analysis.SingleCircuitRef!;
            string existingComment = ParameterHelper.GetCircuitComments(circuit);
            if (!string.IsNullOrEmpty(existingComment))
            {
                // Circuit already has a comment — deselect and do nothing
                uiDoc.Selection.SetElementIds(new List<ElementId>());
                return Result.Succeeded;
            }
            // Circuit exists but no comment — show dialog below
        }
        else
        {
            // No circuit — create one
            circuit = CircuitService.CreateCircuit(doc, new List<FamilyInstance> { fixture });
            if (circuit == null)
                return Result.Failed;
        }

        ShowCommentsDialogAndApply(doc, new List<ElectricalSystem> { circuit });
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

        var circuitsToComment = new List<ElectricalSystem>();

        foreach (var group in fixtures.GroupBy(f => f.Category.BuiltInCategory))
        {
            List<FamilyInstance> groupList = group.ToList();
            var analysis = CircuitService.AnalyzeFixtures(groupList);

            ElectricalSystem? resultCircuit = null;

            if (analysis.AllUncircuited)
            {
                resultCircuit = CircuitService.CreateCircuit(doc, groupList);
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

            if (groupList.Count >= 2)
            {
                Result result = WireMultipleFixtures(doc, groupList, ref message);
                if (result != Result.Succeeded)
                    return result;
            }

            if (resultCircuit != null)
            {
                string existingComment = ParameterHelper.GetCircuitComments(resultCircuit);
                if (string.IsNullOrEmpty(existingComment))
                    circuitsToComment.Add(resultCircuit);
            }
        }

        if (circuitsToComment.Count > 0)
            ShowCommentsDialogAndApply(doc, circuitsToComment);

        return Result.Succeeded;
    }

    private static void ShowCommentsDialogAndApply(Document doc, List<ElectricalSystem> circuits)
    {
        if (!GeneralSettingsCache.Get(doc).ShowCircuitCommentsDialog)
            return;

        var circuitNumbers = string.Join(", ", circuits
            .Select(c => ParameterHelper.GetCircuitNumber(c))
            .Where(n => !string.IsNullOrEmpty(n)));

        var existingComments = CircuitService.GetExistingComments(doc);
        var dialog = new CommentsDialog(existingComments, circuitNumbers);
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.CommentsText))
        {
            foreach (var circuit in circuits)
                CircuitService.SetCircuitComments(doc, circuit, dialog.CommentsText);
        }
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

    private static List<FamilyInstance> GetFixturesOnCircuit(ElectricalSystem circuit)
    {
        List<FamilyInstance> fixtures = new List<FamilyInstance>();

        foreach (Element element in circuit.Elements)
        {
            if (element is FamilyInstance fi &&
                (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                 fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures))
            {
                fixtures.Add(fi);
            }
        }

        return fixtures;
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

        bool rps1 = ParameterHelper.HasRemotePowerSupply(fixture1);
        bool rps2 = ParameterHelper.HasRemotePowerSupply(fixture2);

        if (rps1 && rps2)
        {
            IList<XYZ> straightPoints = new List<XYZ> { c1.Origin, c2.Origin };
            return WireCreationService.CreateWire(doc, straightPoints, WiringType.Chamfer,
                c1, c2, null, null, 0, true, ref message);
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
                    connectorOffset, facingSameDirection, ref message);
            }
        }

        // Priority: tag direction → group centroid → default
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
            // Arc bulges away from the group centroid
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

        wirePoints = ArcCalculator.CalculateArcWirePoints(c1.Origin, c2.Origin, WireConstants.ArcAngleDegrees, arcDirection);
        wiringType = WiringType.Arc;
        return WireCreationService.CreateWire(doc, wirePoints, wiringType, c1, c2, null, null, 0, true, ref message);
    }
}
