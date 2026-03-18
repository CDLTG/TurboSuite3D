#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Driver
{
    /// <summary>
    /// Headless command: pre-select lighting fixtures with Remote Power Supply,
    /// ensure they share an electrical circuit (create one if needed),
    /// then deploy the recommended power supplies.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DriverCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("TurboDriver", "No active document found.");
                    return Result.Failed;
                }

                // Step 1: Get pre-selected lighting fixtures with Remote Power Supply
                var selectedIds = uidoc.Selection.GetElementIds();
                var rpsFixtures = new List<FamilyInstance>();

                foreach (ElementId id in selectedIds)
                {
                    var el = doc.GetElement(id);
                    if (el is FamilyInstance fi
                        && fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures
                        && ParameterHelper.HasRemotePowerSupply(fi))
                    {
                        rpsFixtures.Add(fi);
                    }
                }

                if (rpsFixtures.Count == 0)
                {
                    TaskDialog.Show("TurboDriver",
                        "No lighting fixtures with Remote Power Supply selected.\n\n" +
                        "Select lighting fixtures that have the 'Remote Power Supply' type parameter checked, then run TurboDriver.");
                    return Result.Cancelled;
                }

                // Step 2: Determine the circuit — find existing or create new
                ElectricalSystem circuit = GetOrCreateCircuit(doc, rpsFixtures);
                if (circuit == null)
                {
                    TaskDialog.Show("TurboDriver",
                        "Failed to find or create an electrical circuit for the selected fixtures.");
                    return Result.Failed;
                }

                // Step 3: Build circuit data from the full circuit
                var circuitService = new CircuitCollectorService();
                CircuitData circuitData = circuitService.GetCircuitData(doc, circuit);

                if (circuitData.LightingFixtures.Count == 0)
                {
                    TaskDialog.Show("TurboDriver",
                        $"Circuit {circuitData.CircuitNumber} has no lighting fixtures.");
                    return Result.Cancelled;
                }

                // Step 4: Get driver candidates and recommendation
                var typeService = new FamilyTypeCollectorService();
                var availableTypes = typeService.GetAllLightingDeviceTypes(doc);
                var driverCandidates = typeService.GetDriverCandidates(availableTypes);

                var selectionService = new DriverSelectionService();
                var recommendation = selectionService.GetRecommendation(
                    circuitData.LightingFixtures, driverCandidates);

                if (recommendation == null)
                {
                    // Fixtures have no wattage — Power and Linear Power parameters are missing or zero
                    var missingPowerFixtures = circuitData.LightingFixtures
                        .Where(f => f.EffectiveWattage <= 0)
                        .Select(f => f.TypeMark)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct()
                        .ToList();

                    string typeList = missingPowerFixtures.Count > 0
                        ? string.Join(", ", missingPowerFixtures)
                        : "(no Type Mark)";

                    TaskDialog.Show("TurboDriver",
                        $"No fixtures have wattage defined.\n\n" +
                        $"Set the 'Power' type parameter (or 'Linear Power' instance parameter for linear fixtures) " +
                        $"on the following fixture types: {typeList}");
                    return Result.Cancelled;
                }

                if (!recommendation.HasMatch)
                {
                    TaskDialog.Show("TurboDriver",
                        $"Circuit {circuitData.CircuitNumber}: No matching power supply found.\n\n" +
                        recommendation.WarningMessage);
                    return Result.Cancelled;
                }

                // Step 5: Preserve Switch ID, then delete existing power supplies
                string switchId = CircuitCollectorService.GetCircuitSwitchId(doc, circuitData);
                if (string.IsNullOrEmpty(switchId))
                    switchId = "\u2014"; // em dash default

                var existingDeviceIds = new List<ElementId>();
                foreach (var kvp in circuitData.DevicesByType)
                {
                    foreach (var device in kvp.Value)
                        existingDeviceIds.Add(device.DeviceId);
                }

                if (existingDeviceIds.Count > 0)
                {
                    // Collect wires between existing devices before deleting them
                    var wireIds = DeploymentService.GetWiresBetweenDevices(doc, existingDeviceIds);

                    using (Transaction t = new Transaction(doc, "TurboDriver — Remove existing power supplies"))
                    {
                        t.Start();
                        if (wireIds.Count > 0)
                            doc.Delete(wireIds);
                        doc.Delete(existingDeviceIds);
                        t.Commit();
                    }
                }

                // Step 5.5: Physically split line-based fixtures if enabled
                FixtureSplitService.SplitResult splitResult = null;
                var generalSettings = GeneralSettingsCache.Get(doc);
                if (generalSettings.AutoSplitFixtures)
                {
                    bool hasSplitSegments = recommendation.SubDriverAssignments
                        .SelectMany(a => a.Segments)
                        .Any(s => s.IsSplit);

                    if (hasSplitSegments)
                    {
                        // Store circuit ID before split — the ElectricalSystem reference
                        // becomes stale after deleting the original fixture in the split transaction
                        var circuitId = circuit.Id;

                        var splitService = new FixtureSplitService(doc, doc.ActiveView);
                        using (Transaction splitTx = new Transaction(doc, "TurboDriver — Split linear fixtures"))
                        {
                            splitTx.Start();
                            splitResult = splitService.SplitFixtures(recommendation.SubDriverAssignments, circuit);
                            splitTx.Commit();
                        }

                        // Re-fetch circuit after split
                        circuit = doc.GetElement(circuitId) as ElectricalSystem;
                        if (circuit == null)
                        {
                            TaskDialog.Show("TurboDriver",
                                "The electrical circuit was lost during fixture splitting.\n" +
                                "The fixtures were split but power supply deployment was skipped.");
                            return Result.Failed;
                        }
                    }
                }

                // Step 6: Build single-circuit deployment plan (full recommended count)
                var plan = new DeploymentPlan();
                plan.Circuits.Add(new CircuitDeployment
                {
                    CircuitId = circuit.Id,
                    CircuitNumber = circuitData.CircuitNumber,
                    DriverSymbol = recommendation.RecommendedCandidate.FamilySymbol,
                    QuantityToPlace = recommendation.DriverCount,
                    SwitchId = switchId,
                    Assignments = recommendation.SubDriverAssignments
                });

                // Step 7: Execute deployment (pick point → place → connect → set Switch ID)
                var executor = new DeploymentExecutor();
                executor.Execute(uidoc, plan);

                // Step 8: Tag split fixtures with the original linear length tag
                if (splitResult != null
                    && splitResult.LinearTagTypeId != ElementId.InvalidElementId
                    && splitResult.SplitFixtureIds.Count > 0)
                {
                    TagSplitFixtures(doc, splitResult);
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
                TaskDialog.Show("TurboDriver Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Tags split fixtures with the linear length tag, matching TurboTag's offset logic.
        /// Runs in its own transaction after all other TurboDriver operations are complete.
        /// </summary>
        private static void TagSplitFixtures(Document doc, FixtureSplitService.SplitResult splitResult)
        {
            const string linearTagFamilyName = "AL_Tag_Lighting Fixture (Linear Length)";
            const double linearOffsetFeet = 5.0 / 12.0;

            View activeView = doc.ActiveView;
            ElementId tagTypeId = splitResult.LinearTagTypeId;

            // Determine direction from tag type name (Tag_Top → Up, Tag_Bottom → Down)
            var tagSymbol = doc.GetElement(tagTypeId) as FamilySymbol;
            bool isTopTag = tagSymbol != null
                && string.Equals(tagSymbol.Name, "Tag_Top", StringComparison.OrdinalIgnoreCase);

            using (Transaction t = new Transaction(doc, "TurboDriver — Tag split fixtures"))
            {
                t.Start();

                foreach (var fixtureId in splitResult.SplitFixtureIds)
                {
                    var fixture = doc.GetElement(fixtureId) as FamilyInstance;
                    if (fixture?.Location is not LocationCurve locCurve) continue;

                    // Delete any tags that were copied with the fixture during split
                    var existingTagIds = new FilteredElementCollector(doc, activeView.Id)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>()
                        .Where(tag =>
                        {
                            if (!tag.GetTaggedLocalElementIds().Contains(fixtureId))
                                return false;
                            if (doc.GetElement(tag.GetTypeId()) is FamilySymbol sym
                                && string.Equals(sym.FamilyName, linearTagFamilyName,
                                    StringComparison.OrdinalIgnoreCase))
                                return true;
                            return false;
                        })
                        .Select(tag => tag.Id)
                        .ToList();

                    if (existingTagIds.Count > 0)
                        doc.Delete(existingTagIds);

                    // Place tag at midpoint
                    XYZ midpoint = locCurve.Curve.Evaluate(0.5, true);
                    var newTag = IndependentTag.Create(
                        doc, tagTypeId, activeView.Id,
                        new Reference(fixture),
                        addLeader: false,
                        TagOrientation.Horizontal,
                        midpoint);

                    if (newTag == null) continue;

                    // Apply TurboTag's linear offset (5" perpendicular to fixture line)
                    bool isReversed = IsLineReversed(locCurve.Curve);
                    double offsetVal = isReversed ? -linearOffsetFeet : linearOffsetFeet;
                    XYZ localOffset = isTopTag
                        ? new XYZ(0, offsetVal, 0)
                        : new XYZ(0, -offsetVal, 0);
                    XYZ globalOffset = TransformToGlobal(fixture, localOffset);

                    if (!globalOffset.IsZeroLength())
                        ElementTransformUtils.MoveElement(doc, newTag.Id, globalOffset);
                }

                t.Commit();
            }
        }

        /// <summary>
        /// Checks if a line-based fixture's curve runs in a "reversed" direction
        /// (right-to-left or bottom-to-top). Matches TagPlacementService.IsLineReversed.
        /// </summary>
        private static bool IsLineReversed(Curve curve)
        {
            XYZ direction = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
            return direction.X < -0.001 || (Math.Abs(direction.X) < 0.001 && direction.Y < -0.001);
        }

        /// <summary>
        /// Converts a fixture-local offset to global coordinates using BasisX rotation angle.
        /// Matches TagPlacementService.TransformToGlobal.
        /// </summary>
        private static XYZ TransformToGlobal(FamilyInstance fixture, XYZ localOffset)
        {
            Transform transform = fixture.GetTransform();
            double angle = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            return new XYZ(
                localOffset.X * cos - localOffset.Y * sin,
                localOffset.X * sin + localOffset.Y * cos,
                0);
        }

        /// <summary>
        /// Find the common electrical circuit for the selected fixtures, or create one if none exists.
        /// Returns null if fixtures are on multiple different circuits or circuit creation fails.
        /// </summary>
        private static ElectricalSystem GetOrCreateCircuit(Document doc, List<FamilyInstance> fixtures)
        {
            // Collect existing circuits from fixtures
            var circuitSet = new Dictionary<ElementId, ElectricalSystem>();
            var uncircuitedFixtures = new List<FamilyInstance>();

            foreach (var fixture in fixtures)
            {
                var systems = fixture.MEPModel?.GetElectricalSystems();
                ElectricalSystem es = null;
                if (systems != null)
                {
                    foreach (ElectricalSystem s in systems)
                    {
                        es = s;
                        break;
                    }
                }

                if (es != null)
                {
                    circuitSet[es.Id] = es;
                }
                else
                {
                    uncircuitedFixtures.Add(fixture);
                }
            }

            // Case: All fixtures already on the same circuit
            if (circuitSet.Count == 1 && uncircuitedFixtures.Count == 0)
                return circuitSet.Values.First();

            // Case: Fixtures on multiple different circuits
            if (circuitSet.Count > 1)
            {
                TaskDialog.Show("TurboDriver",
                    $"Selected fixtures are on {circuitSet.Count} different circuits.\n" +
                    "Select fixtures from a single circuit.");
                return null;
            }

            // Case: Some fixtures on a circuit, some not — add uncircuited to existing
            if (circuitSet.Count == 1 && uncircuitedFixtures.Count > 0)
            {
                var existingCircuit = circuitSet.Values.First();
                using (Transaction t = new Transaction(doc, "TurboDriver — Add fixtures to circuit"))
                {
                    t.Start();
                    var addSet = new ElementSet();
                    foreach (var fi in uncircuitedFixtures)
                        addSet.Insert(fi);
                    existingCircuit.AddToCircuit(addSet);
                    t.Commit();
                }
                return existingCircuit;
            }

            // Case: No fixtures on any circuit — create a new one
            using (Transaction t = new Transaction(doc, "TurboDriver — Create electrical circuit"))
            {
                t.Start();

                // Create circuit from all fixture element IDs
                var fixtureIds = fixtures.Select(f => f.Id).ToList();
                var newCircuit = ElectricalSystem.Create(doc, fixtureIds, ElectricalSystemType.PowerCircuit);
                if (newCircuit == null)
                {
                    t.RollBack();
                    TaskDialog.Show("TurboDriver", "Failed to create electrical circuit.");
                    return null;
                }

                t.Commit();
                return newCircuit;
            }
        }
    }
}
