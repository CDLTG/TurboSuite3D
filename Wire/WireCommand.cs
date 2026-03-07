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
using TurboSuite.Wire.Constants;
using TurboSuite.Wire.Helpers;
using TurboSuite.Wire.Services;

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
                return Result.Succeeded;
            }

            List<FamilyInstance> preSelectedFixtures = GetPreSelectedFixtures(uiDoc);

            if (preSelectedFixtures.Count >= 2)
            {
                foreach (var group in preSelectedFixtures.GroupBy(f => f.Category.BuiltInCategory))
                {
                    List<FamilyInstance> groupList = group.ToList();
                    if (groupList.Count >= 2)
                    {
                        Result result = WireMultipleFixtures(doc, groupList, ref message);
                        if (result != Result.Succeeded)
                            return result;
                    }
                }
                return Result.Succeeded;
            }
            else
            {
                return ManualSelection(uiDoc, doc, ref message);
            }
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
