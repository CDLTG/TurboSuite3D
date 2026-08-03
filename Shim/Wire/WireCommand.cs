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
using TurboSuite.Wire.Views;

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
                    List<FamilyInstance> fixturesOnCircuit = GetFixturesOnCircuit(circuit);
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

                // Show comments dialog for pre-selected circuits that have no comment
                var circuitsToComment = preSelectedCircuits
                    .Where(c => string.IsNullOrEmpty(ParameterHelper.GetCircuitComments(c)))
                    .ToList();
                if (circuitsToComment.Count > 0)
                {
                    if (!ShowCommentsDialogAndApply(doc, circuitsToComment))
                    {
                        txGroup.RollBack();
                        return Result.Cancelled;
                    }
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

        if (!ShowCommentsDialogAndApply(doc, new List<ElectricalSystem> { circuit }))
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

        var circuitsToComment = new List<ElectricalSystem>();

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

        if (mixedCircuit != null)
        {
            string existingComment = ParameterHelper.GetCircuitComments(mixedCircuit);
            if (string.IsNullOrEmpty(existingComment))
                circuitsToComment.Add(mixedCircuit);
        }

        if (circuitsToComment.Count > 0)
        {
            if (!ShowCommentsDialogAndApply(doc, circuitsToComment))
            {
                txGroup.RollBack();
                return Result.Cancelled;
            }
        }

        txGroup.Assimilate();
        return Result.Succeeded;
    }

    /// <summary>
    /// Returns false if the user cancelled the dialog (caller should roll back).
    /// Returns true if comments were applied, dialog was skipped, or left empty.
    /// </summary>
    private static bool ShowCommentsDialogAndApply(Document doc, List<ElectricalSystem> circuits)
    {
        if (!GeneralSettingsCache.Get(doc).ShowCircuitCommentsDialog)
            return true;

        var circuitNumbers = string.Join(", ", circuits
            .Select(c => ParameterHelper.GetCircuitNumber(c))
            .Where(n => !string.IsNullOrEmpty(n)));

        var existingComments = CircuitService.GetExistingComments(doc);
        var panels = CircuitService.GetAllPanels(doc);
        // Default the panel dropdown to the last circuit's choice — a real panel, or
        // <None> when the previous circuit was deliberately left unassigned. Exclude the
        // circuits being wired now so they reflect the prior state, not themselves.
        var (autoPanel, preferNone) = CircuitService.FindLastPanelChoice(
            doc, circuits.Select(c => c.Id).ToList());

        // Resolve each circuit's live base room (linked Rooms, region fallback in 2D)
        // the same way TurboZones does — first lighting/electrical fixture on the
        // circuit. Blank is valid.
        var regionFallback = new RegionRoomLookupService(doc);
        var roomCache = new LinkedRoomFinderService.RoomLookupCache(doc, regionFallback);
        var baseRooms = circuits.ToDictionary(
            c => c,
            c => ResolveBaseRoom(c, roomCache));
        var existingOverrides = RoomOverrideStorageService.Load(doc);

        // Each circuit's effective room = its existing override if set, else its base
        // room. Prefill that when all circuits agree (so a saved override is visible and
        // preserved when the field is left alone); when they disagree, show <varies>
        // rather than misleadingly stamping the first circuit's room across the batch.
        // Left untouched, <varies> is a no-op (below); typing over it applies to all.
        // Kept as plain ASCII so an accidental edit is trivial to retype/correct.
        const string VariesPlaceholder = "<varies>";
        string EffectiveRoom(ElectricalSystem c) =>
            (existingOverrides.TryGetValue(c.UniqueId, out var ov) && !string.IsNullOrWhiteSpace(ov)
                ? ov
                : baseRooms[c]) ?? string.Empty;

        string resolvedRoom = string.Empty;
        if (circuits.Count > 0)
        {
            var distinctRooms = circuits
                .Select(c => EffectiveRoom(c).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            resolvedRoom = distinctRooms.Count == 1 ? distinctRooms[0] : VariesPlaceholder;
        }
        var roomNames = CollectProjectRoomNames(doc, regionFallback);

        var dialog = new CommentsDialog(existingComments, panels, autoPanel, circuitNumbers,
            resolvedRoom, roomNames, preferNone);
        if (dialog.ShowDialog() == true)
        {
            if (!string.IsNullOrEmpty(dialog.CommentsText))
            {
                foreach (var circuit in circuits)
                    CircuitService.SetCircuitComments(doc, circuit, dialog.CommentsText);
            }

            // Room Override: only act when the user actually changed the field from what
            // was prefilled. An untouched field must be a true no-op — otherwise, in a
            // multi-circuit batch it would stamp the first circuit's prefilled room onto
            // circuits whose base room differs, and it would clear an existing override
            // the user left alone. When the field IS changed, apply per-circuit: entered
            // text that equals a circuit's own base room clears it (falls back to
            // geometry); anything else (including blank) is written as-is / cleared.
            string enteredRoom = (dialog.RoomOverrideText ?? string.Empty).Trim();
            bool userChangedRoom = !string.Equals(enteredRoom,
                (resolvedRoom ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
            if (userChangedRoom)
            {
                var overrideChanges = new Dictionary<string, string>();
                foreach (var circuit in circuits)
                {
                    string baseRoom = (baseRooms[circuit] ?? string.Empty).Trim();
                    bool isOverride = enteredRoom.Length > 0
                        && !string.Equals(enteredRoom, baseRoom, StringComparison.OrdinalIgnoreCase);
                    overrideChanges[circuit.UniqueId] = isOverride ? enteredRoom : string.Empty;
                }
                if (overrideChanges.Values.Any(v => !string.IsNullOrEmpty(v))
                    || existingOverrides.Keys.Any(k => overrideChanges.ContainsKey(k)))
                {
                    using var t = new Transaction(doc, "TurboWire — Room override");
                    t.Start();
                    RoomOverrideStorageService.Upsert(doc, overrideChanges);
                    t.Commit();
                }
            }

            if (dialog.UnassignPanel)
            {
                // User picked <None> — strip any auto-assigned panel (DMX/DALI etc.)
                foreach (var circuit in circuits)
                {
                    if (circuit.BaseEquipment != null)
                        CircuitService.ClearCircuitPanel(doc, circuit);
                }
            }
            else if (dialog.SelectedPanel != null)
            {
                // Re-assign panel if user picked a different one
                foreach (var circuit in circuits)
                {
                    if (circuit.BaseEquipment?.Id != dialog.SelectedPanel.Id)
                        CircuitService.SetCircuitPanel(doc, circuit, dialog.SelectedPanel);
                }
            }

            return true;
        }

        return false; // User cancelled
    }

    /// <summary>
    /// Live base room for a circuit: the room resolved from its first
    /// lighting/electrical fixture (matches TurboZones' convention). Empty if the
    /// circuit has no such fixture or no room resolves.
    /// </summary>
    private static string ResolveBaseRoom(ElectricalSystem circuit,
        LinkedRoomFinderService.RoomLookupCache roomCache)
    {
        var fixtures = GetFixturesOnCircuit(circuit);
        if (fixtures.Count == 0)
            return string.Empty;
        return roomCache.FindRoomName(fixtures[0]) ?? string.Empty;
    }

    /// <summary>
    /// Distinct, sorted room names for the Room Override search/autofill: real Rooms
    /// across the host document and all linked models, plus "Room Region" names from
    /// the 2D fallback so drafting jobs (which have no Rooms) still get suggestions.
    /// </summary>
    private static List<string> CollectProjectRoomNames(Document doc,
        RegionRoomLookupService regionFallback)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(Document d)
        {
            foreach (var room in new FilteredElementCollector(d)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfClass(typeof(SpatialElement))
                .Cast<Autodesk.Revit.DB.Architecture.Room>())
            {
                string? name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!.Trim());
            }
        }

        Collect(doc);
        foreach (var link in new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc != null)
                Collect(linkDoc);
        }

        // 2D drafting: "Room Region" names, so jobs with no Room elements still list.
        foreach (var name in regionFallback.RoomNames)
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
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
