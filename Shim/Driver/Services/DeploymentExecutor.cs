#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Result of a TurboDriver deployment execution.
    /// </summary>
    public class DeploymentResult
    {
        public int TotalPlaced { get; set; }
        public int TotalConnected { get; set; }
        public int TotalSwitchIdSet { get; set; }
        public int TotalTagsPlaced { get; set; }
        public int TotalWiresPlaced { get; set; }
        public int TotalFailed { get; set; }
        public bool WasCancelled { get; set; }
        public List<ElementId> PlacedInstanceIds { get; set; } = new List<ElementId>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Orchestrates TurboDriver deployment.
    /// User picks ONE point or selects an existing power supply; all new power supplies are placed in a column, 9.5" apart.
    /// </summary>
    public class DeploymentExecutor
    {
        // Column spacing at the 1/4" reference (9.5"). Each device draws as a nested annotation that
        // scales with the view, so the spacing is × K of the placement view (PlanScale).
        private const double SpacingRefFt = 9.5 / 12.0;

        /// <summary>
        /// Execute the TurboDriver deployment: pick one point, place all power supplies in a column.
        /// </summary>
        public DeploymentResult Execute(UIDocument uidoc, DeploymentPlan plan)
        {
            Document doc = uidoc.Document;
            var service = new DeploymentService(doc);
            var result = new DeploymentResult();

            // Stacking rule for a rotated view: align the column to the PROJECT/model (so an
            // odd-angle view shows it tilted to match the rotated geometry), EXCEPT at square
            // crop rotations (0/90/180/270) where model-down would lay the column sideways — there
            // we snap it to screen-down. Device orientation follows the same rule. Identity in an
            // un-rotated view. `stackDownUnit` is the unit "down the column" vector in model coords.
            View activeView = doc.ActiveView;

            // Host new drivers on the active plan's level. The driver families are level-based:
            // placed without a level they arrive with Host = None and read "Elevation from Level"
            // as their full internal Z (out of range / floating). Null for non-plan views, which
            // fall back to the bare-point placement. See DeploymentService.PlacePowerSupply.
            Level hostLevel = (activeView as ViewPlan)?.GenLevel;

            double cropAngle = ViewOrientationHelper.GetViewRotation(activeView);
            bool snapToScreen = ViewOrientationHelper.IsNearRightAngle(cropAngle);
            XYZ stackDownUnit = snapToScreen
                ? ViewOrientationHelper.ScreenOffsetToModel(activeView, new XYZ(0, -1, 0))
                : new XYZ(0, -1, 0);
            double deviceRotation = snapToScreen ? cropAngle : 0.0;
            double spacingFt = SpacingRefFt * PlanScaleHelper.Of(activeView).K;

            // Pick origin: select an existing power supply (new ones placed 9.5" below)
            // or press Escape to pick a bare point instead
            XYZ origin;
            try
            {
                var reference = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new LightingDeviceSelectionFilter(),
                    $"Select existing power supply to stack below, or press Esc to pick a point");

                var anchor = doc.GetElement(reference.ElementId) as FamilyInstance;
                var anchorLocation = GeometryHelper.GetFixtureLocation(anchor);
                // First new supply sits one spacing "down the column" from the anchor.
                origin = new XYZ(
                    anchorLocation.X + stackDownUnit.X * spacingFt,
                    anchorLocation.Y + stackDownUnit.Y * spacingFt,
                    anchorLocation.Z);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User pressed Escape — fall back to picking a bare point
                try
                {
                    var picked = uidoc.Selection.PickPoint(
                        $"Pick origin for {plan.TotalQuantity} power supplies");

                    // PickPoint's Z is the active work plane elevation, which may sit outside the
                    // view's primary range — so the device (only a connector + nested generic
                    // annotation, no 3D geometry) vanishes while its view-owned tags still draw.
                    // This bites in our standard RCP setup: the view is based on the floor level
                    // (0') but the range is cut at +6' looking up at a +10' ceiling, so the level
                    // itself is below the range. Snap Z to the active view's display elevation
                    // (RCP top / floor-plan bottom — see PlanDisplayElevation). Falls back to the
                    // picked Z for views with no resolvable range (drafting/3D/section).
                    double? displayZ = PlanDisplayElevation.Of(doc, doc.ActiveView);
                    origin = new XYZ(picked.X, picked.Y, displayZ ?? picked.Z);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    result.WasCancelled = true;
                    return result;
                }
            }

            // Revit's NewFamilyInstance does NOT reliably honor the point's Z for a level-based
            // family — it inherits the family's sticky "Elevation from Level" default from the last
            // interactive placement (verified in-model: a fresh session dropped drivers at 1356',
            // but after ONE manual 10' placement every subsequent API placement inherited 10',
            // ignoring the Z we passed). So point.Z is only a hint; we authoritatively SET
            // Elevation-from-Level on each placed driver. The target is origin.Z — built in the
            // internal-origin frame by PlanDisplayElevation (RCP top / floor-plan bottom), or copied
            // from the stack anchor — expressed relative to the host level: origin.Z minus the
            // level's own ProjectElevation (e.g. -3.552' - (-13.552') = 10'). Null for non-plan views.
            double? forcedElevFromLevel = hostLevel != null
                ? origin.Z - hostLevel.ProjectElevation
                : (double?)null;

            // Place all power supplies in a single transaction
            int globalIndex = 0;

            // Consecutive device pairs to wire AFTER placement commits (see note below).
            var wirePairs = new List<(FamilyInstance First, FamilyInstance Second)>();
            bool placementCommitted = false;
            bool placementRolledBack = false;

            using (Transaction trans = new Transaction(doc, "TurboDriver — Place Power Supplies"))
            {
                trans.Start();
                try
                {
                    foreach (var circuit in plan.Circuits)
                    {
                        // Strip any existing suffix from base Switch ID (e.g., "X01a" → "X01")
                        string baseSwitchId = StripSwitchIdSuffix(circuit.SwitchId);
                        var circuitInstances = new List<FamilyInstance>();

                        for (int i = 0; i < circuit.QuantityToPlace; i++)
                        {
                            // Column layout: each instance offset "down the column" by 9.5" along
                            // the stack direction (model-aligned, or screen-down at square angles).
                            double dist = globalIndex * spacingFt;
                            XYZ point = new XYZ(
                                origin.X + stackDownUnit.X * dist,
                                origin.Y + stackDownUnit.Y * dist,
                                origin.Z);

                            var instance = service.PlacePowerSupply(point, circuit.DriverSymbol, hostLevel);
                            if (instance == null)
                            {
                                result.TotalFailed++;
                                result.Warnings.Add($"Circuit {circuit.CircuitNumber}: Failed to place instance.");
                                globalIndex++;
                                continue;
                            }

                            // Authoritatively pin the elevation — do NOT trust the placed Z (Revit
                            // may have used the family's sticky default; see forcedElevFromLevel).
                            if (forcedElevFromLevel.HasValue)
                                service.SetElevationFromLevel(instance, forcedElevFromLevel.Value);

                            // Match the stack rule: upright on screen at square angles, else model-aligned.
                            if (System.Math.Abs(deviceRotation) > 1e-9)
                            {
                                Line axis = Line.CreateBound(point, point + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, instance.Id, axis, deviceRotation);
                            }

                            result.TotalPlaced++;
                            result.PlacedInstanceIds.Add(instance.Id);
                            circuitInstances.Add(instance);

                            // Add to circuit
                            bool connected = service.AddToCircuit(instance, circuit.CircuitId);
                            if (connected)
                            {
                                result.TotalConnected++;
                            }
                            else
                            {
                                result.Warnings.Add(
                                    $"Circuit {circuit.CircuitNumber}: Placed but could not add to circuit.");
                            }

                            // Set Switch ID with suffix when multiple power supplies
                            string switchId = baseSwitchId;
                            if (!string.IsNullOrEmpty(baseSwitchId) && circuit.QuantityToPlace > 1)
                                switchId = baseSwitchId + (char)('a' + i);

                            bool switchSet = service.SetSwitchId(instance, switchId);
                            if (switchSet)
                            {
                                result.TotalSwitchIdSet++;
                            }
                            else if (!string.IsNullOrEmpty(switchId))
                            {
                                result.Warnings.Add(
                                    $"Circuit {circuit.CircuitNumber}: Could not set Switch ID '{switchId}'.");
                            }

                            // Tag the device: Switchleg tag only on first, SwitchID tag on all
                            bool isFirst = (i == 0);
                            bool multipleDevices = circuit.QuantityToPlace > 1;
                            int tagsPlaced = service.TagDevice(instance, doc.ActiveView,
                                includeSwitchleg: !multipleDevices || isFirst);
                            result.TotalTagsPlaced += tagsPlaced;

                            int expectedTags = (!multipleDevices || isFirst) ? 2 : 1;
                            if (tagsPlaced < expectedTags)
                            {
                                result.Warnings.Add(
                                    $"{tagsPlaced}/{expectedTags} tags placed. " +
                                    $"Ensure tag families are loaded whose '{ParameterNames.TurboSuiteRole}' is " +
                                    $"'{Roles.SwitchIdTag}' or '{Roles.DeviceSwitchlegTag}'.");
                            }

                            globalIndex++;
                        }

                        // Collect consecutive pairs; wire them AFTER this transaction commits.
                        for (int w = 1; w < circuitInstances.Count; w++)
                            wirePairs.Add((circuitInstances[w - 1], circuitInstances[w]));
                    }

                    if (CommitGuard.TryCommit(trans)) placementCommitted = true;
                    else placementRolledBack = true;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Transaction failed: {ex.Message}");
                    if (trans.HasStarted())
                        trans.RollBack();
                }
            }

            // Outside the catch above, so it reaches DriverCommand, which returns Failed and discards the run.
            if (placementRolledBack) throw new WorksharingException(WorksharingMessages.RolledBack);

            // Wire each pair in its OWN transaction. Committing each wire separately gives Revit the
            // post-commit regeneration that clips the terminal wire end to the family boundary.
            // Wiring the whole chain inside the shared placement transaction above leaves the last
            // driver's wire drawn to its connector center — a stray "tail" into the final driver.
            if (placementCommitted)
            {
                foreach (var (d1, d2) in wirePairs)
                {
                    try
                    {
                        using Transaction wireTx = new Transaction(doc, "TurboDriver — Wire Power Supplies");
                        wireTx.Start();
                        bool wired = service.CreateWireBetween(d1, d2, doc.ActiveView);
                        if (!CommitGuard.TryCommit(wireTx))
                            result.Warnings.Add($"Could not wire a power-supply pair: {WorksharingMessages.RolledBack}");
                        else if (wired)
                            result.TotalWiresPlaced++;
                    }
                    catch (Exception ex)
                    {
                        // One failed wire shouldn't abort the rest or surface a generic crash dialog.
                        result.Warnings.Add($"Could not wire a power-supply pair: {ex.Message}");
                    }
                }
            }

            // Select placed instances for easy inspection
            if (result.PlacedInstanceIds.Count > 0)
                uidoc.Selection.SetElementIds(result.PlacedInstanceIds);

            // Only show dialog if something went wrong
            if (result.TotalFailed > 0 || result.Warnings.Count > 0)
            {
                var sb = new StringBuilder();
                if (result.TotalFailed > 0)
                    sb.AppendLine($"Failed to place: {result.TotalFailed}");
                foreach (var w in result.Warnings)
                    sb.AppendLine(w);
                TaskDialog.Show("TurboDriver", sb.ToString());
            }

            return result;
        }

        /// <summary>
        /// Strip a trailing lowercase letter suffix from a Switch ID (e.g., "X01a" → "X01").
        /// Only strips if the last char is a-z and the preceding char is not a-z
        /// (to avoid stripping from IDs that are entirely alphabetic).
        /// </summary>
        private static string StripSwitchIdSuffix(string switchId)
        {
            if (string.IsNullOrEmpty(switchId) || switchId.Length < 2)
                return switchId;

            char last = switchId[switchId.Length - 1];
            char secondLast = switchId[switchId.Length - 2];

            if (last >= 'a' && last <= 'z' && !(secondLast >= 'a' && secondLast <= 'z'))
                return switchId.Substring(0, switchId.Length - 1);

            return switchId;
        }
    }

    /// <summary>
    /// Selection filter that accepts only Lighting Device family instances.
    /// </summary>
    internal class LightingDeviceSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is FamilyInstance fi
                && fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingDevices;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
