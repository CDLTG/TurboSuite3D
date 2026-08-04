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
    /// Near-headless command: pre-select lighting fixtures with Remote Power Supply,
    /// ensure they share an electrical circuit (create one if needed), optionally review the
    /// circuit's comment / room override / panel via the shared circuit-info dialog (gated by
    /// the General setting), then deploy the recommended power supplies.
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

                // Pipeline: select RPS fixtures → get/create circuit → collect circuit data →
                // recommend driver → delete existing devices → split linear fixtures → deploy → tag
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

                ElectricalSystem circuit = GetOrCreateCircuit(doc, rpsFixtures);
                if (circuit == null)
                {
                    TaskDialog.Show("TurboDriver",
                        "Failed to find or create an electrical circuit for the selected fixtures.");
                    return Result.Failed;
                }

                var circuitService = new CircuitCollectorService();
                CircuitData circuitData = circuitService.GetCircuitData(doc, circuit);

                if (circuitData.LightingFixtures.Count == 0)
                {
                    TaskDialog.Show("TurboDriver",
                        "No lighting fixtures found on the circuit.");
                    return Result.Cancelled;
                }

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
                    // Deliberately does NOT name the circuit. Every abort above this point returns
                    // Cancelled, which makes Revit discard whatever GetOrCreateCircuit committed — so
                    // when it created the circuit, its number refers to something that stops existing
                    // the moment this dialog closes. Don't send the user looking for it.
                    TaskDialog.Show("TurboDriver",
                        "No matching power supply found for the selected fixtures.\n\n" +
                        recommendation.WarningMessage);
                    return Result.Cancelled;
                }

                // Circuit-info dialog (comment / room override / panel), now that we know the
                // circuit can actually be built. Setting-gated. Nothing destructive has happened
                // yet, so cancelling here discards a freshly created circuit cleanly — the same
                // Cancelled → Revit-rolls-back invariant every abort above relies on.
                if (CircuitInfoService.PromptAndApply(doc, new[] { circuit }, "TurboDriver")
                    == CircuitInfoResult.Cancelled)
                    return Result.Cancelled;

                // Preserve Switch ID before deleting existing power supplies
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

                // Split line-based fixtures into sub-driver segments if enabled
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
                                "The electrical circuit was lost during fixture splitting.\n\n" +
                                "No changes were made.");

                            // Failed is DELIBERATE — do not "fix" this to Succeeded. Revit discards a
                            // command's committed changes on a Cancelled/Failed return (see CLAUDE.md
                            // "Command return value rolls back saves"), which here is exactly what we
                            // want: the split above already deleted the user's original fixture, so
                            // persisting this state would leave them with orphaned, uncircuited segments
                            // and no original to recover. Rolling back restores the known-good fixture.
                            // The cost is that a re-run hits the same failure — the real fix is to detect
                            // the copy/AddToCircuit failure in FixtureSplitService BEFORE it deletes the
                            // original (see its "prevent empty circuit" guard), so this state can't arise.
                            return Result.Failed;
                        }
                    }
                }

                var driverSymbol = doc.GetElement(
                    recommendation.RecommendedCandidate.SymbolRef.ToElementId()) as FamilySymbol;

                var plan = new DeploymentPlan();
                plan.Circuits.Add(new CircuitDeployment
                {
                    CircuitId = circuit.Id,
                    CircuitNumber = circuitData.CircuitNumber,
                    DriverSymbol = driverSymbol,
                    QuantityToPlace = recommendation.DriverCount,
                    SwitchId = switchId,
                    Assignments = recommendation.SubDriverAssignments
                });

                var executor = new DeploymentExecutor();
                executor.Execute(uidoc, plan);

                if (splitResult != null
                    && splitResult.LinearTagTypeId != ElementId.InvalidElementId
                    && splitResult.SplitFixtureIds.Count > 0)
                {
                    TagSplitFixtures(doc, splitResult);
                }

                // Re-select the circuit's lighting fixtures so the user can immediately
                // assign them to switches (e.g., via TurboNumber / Switch ID workflow).
                ReselectCircuitFixtures(uidoc, circuit.Id);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                // The Failed return discards every transaction this run committed — device deletion,
                // fixture splits, deployment — so the model is back where it started. Say so: after a
                // mid-operation crash the user's first question is what state their model is in.
                TaskDialog.Show("TurboDriver Error",
                    $"An unexpected error occurred:\n{ex.Message}\n\nNo changes were made.");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Re-selects the lighting fixtures on the given circuit so the user can
        /// immediately assign them to switches without re-picking.
        /// </summary>
        private static void ReselectCircuitFixtures(UIDocument uidoc, ElementId circuitId)
        {
            if (uidoc.Document.GetElement(circuitId) is not ElectricalSystem circuit) return;

            var fixtureIds = new List<ElementId>();
            foreach (Element el in circuit.Elements)
            {
                if (el is FamilyInstance fi
                    && fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures)
                {
                    fixtureIds.Add(fi.Id);
                }
            }

            if (fixtureIds.Count > 0)
                uidoc.Selection.SetElementIds(fixtureIds);
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
            double angle = GeometryHelper.GetTransformAngle(fixture.GetTransform());
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

            // All on same circuit
            if (circuitSet.Count == 1 && uncircuitedFixtures.Count == 0)
                return circuitSet.Values.First();

            // Multiple circuits — ambiguous
            if (circuitSet.Count > 1)
            {
                TaskDialog.Show("TurboDriver",
                    $"Selected fixtures are on {circuitSet.Count} different circuits.\n" +
                    "Select fixtures from a single circuit.");
                return null;
            }

            // Mixed: add uncircuited fixtures to the existing circuit
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

            // None circuited — create a new circuit. CircuitService assigns the remembered
            // panel default, honoring a deliberate <None> the same way TurboWire does (a DMX/
            // DALI circuit left unassigned last time leaves this one unassigned too).
            var newCircuit = CircuitService.CreateCircuit(doc, fixtures);
            if (newCircuit == null)
                TaskDialog.Show("TurboDriver", "Failed to create electrical circuit.");
            return newCircuit;
        }
    }
}
