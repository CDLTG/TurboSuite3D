#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;

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
        private const double SpacingFt = 9.5 / 12.0; // 9.5 inches in feet


        /// <summary>
        /// Absolute elevation at which to drop annotation-only devices so they display in the
        /// active plan view WITHOUT bleeding into the companion view. Placing at the cut plane
        /// isn't safe: an RCP cut (e.g. +6') often falls inside a floor plan's range, so a driver
        /// placed in one view shows up in the other. Instead we go to the range extreme that
        /// points away from the companion view — RCPs look up, so the TOP clip plane (near the
        /// ceiling, above a floor plan's top); floor plans look down, so the BOTTOM clip plane
        /// (near the floor, below an RCP's bottom). Both are primary-range boundaries, so the
        /// device is still guaranteed visible in its own view. Null for views with no resolvable
        /// range (drafting/3D/section).
        ///
        /// This is an ABSOLUTE, internal-origin elevation (model coordinates) — the frame every
        /// LocationPoint.Z uses — so it must be built from Level.ProjectElevation, NOT Level.Elevation.
        /// Level.Elevation is measured from the project's *elevation base*, which a survey/shared/
        /// relocated datum can inflate away from the internal origin (verified in-model via TurboSpike:
        /// a level reading .Elevation = 1346' but .ProjectElevation = -13.5', with all geometry sitting
        /// in the -13.5' frame). Using .Elevation there shoved drivers ~1360' into the sky. The two are
        /// equal in an un-relocated project, so ProjectElevation is correct universally.
        /// </summary>
        private static double? GetDisplayElevation(Document doc, View view)
        {
            var plan = view as ViewPlan;
            if (plan == null)
                return null;

            PlanViewPlane whichPlane = plan.ViewType == ViewType.CeilingPlan
                ? PlanViewPlane.TopClipPlane
                : PlanViewPlane.BottomClipPlane;

            PlanViewRange range = plan.GetViewRange();

            // Resolve one range plane to an absolute internal-origin elevation, or null if its level
            // can't be resolved. The plane's level can be a real level OR one of Revit's "Level
            // Above"/"Level Below" sentinel ids (e.g. an RCP with Top = Level Above, offset 0);
            // sentinels don't resolve via GetElement, so map them to the neighbouring level.
            // ProjectElevation (internal origin), not Elevation (elevation base) — see method summary.
            double? ResolvePlane(PlanViewPlane p)
            {
                ElementId levelId = range.GetLevelId(p);
                Level level;
                if (levelId.Equals(PlanViewRange.LevelAbove))
                    level = AdjacentLevel(doc, plan.GenLevel, above: true);
                else if (levelId.Equals(PlanViewRange.LevelBelow))
                    level = AdjacentLevel(doc, plan.GenLevel, above: false);
                else
                    level = doc.GetElement(levelId) as Level;

                return level != null ? level.ProjectElevation + range.GetOffset(p) : (double?)null;
            }

            // Primary: the range extreme pointing away from the companion view. If that plane's level
            // can't be resolved — the classic case being a top-floor RCP whose Top is "Level Above"
            // with no level above — degrade WITHIN the plan rather than to the raw picked Z (which is
            // just wherever the work plane sits, out of range). The cut plane always lies inside the
            // primary range, so it's guaranteed visible; GenLevel is the last-ditch anchor.
            return ResolvePlane(whichPlane)
                ?? ResolvePlane(PlanViewPlane.CutPlane)
                ?? (plan.GenLevel != null
                        ? plan.GenLevel.ProjectElevation + range.GetOffset(whichPlane)
                        : (double?)null);
        }

        /// <summary>
        /// The nearest level directly above (or below) the given level by elevation, or null if
        /// there is none. Used to resolve view-range "Level Above"/"Level Below" sentinels.
        /// Ordered by ProjectElevation (internal origin) so the neighbour search matches the frame
        /// GetDisplayElevation builds in — a per-level elevation base can otherwise reorder .Elevation.
        /// </summary>
        private static Level AdjacentLevel(Document doc, Level from, bool above)
        {
            if (from == null)
                return null;

            double baseElev = from.ProjectElevation;
            const double Tol = 1e-6;
            Level best = null;
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                if (!(el is Level lvl))
                    continue;
                double e = lvl.ProjectElevation;
                if (above)
                {
                    if (e > baseElev + Tol && (best == null || e < best.ProjectElevation))
                        best = lvl;
                }
                else
                {
                    if (e < baseElev - Tol && (best == null || e > best.ProjectElevation))
                        best = lvl;
                }
            }
            return best;
        }

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
                    anchorLocation.X + stackDownUnit.X * SpacingFt,
                    anchorLocation.Y + stackDownUnit.Y * SpacingFt,
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
                    // (RCP top / floor-plan bottom — see GetDisplayElevation). Falls back to the
                    // picked Z for views with no resolvable range (drafting/3D/section).
                    double? displayZ = GetDisplayElevation(doc, doc.ActiveView);
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
            // internal-origin frame by GetDisplayElevation (RCP top / floor-plan bottom), or copied
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
                            double dist = globalIndex * SpacingFt;
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
                                    "Ensure tag families are loaded: AL_Tag_Lighting Device (SwitchID), AL_Tag_Lighting Device (Switchleg).");
                            }

                            globalIndex++;
                        }

                        // Collect consecutive pairs; wire them AFTER this transaction commits.
                        for (int w = 1; w < circuitInstances.Count; w++)
                            wirePairs.Add((circuitInstances[w - 1], circuitInstances[w]));
                    }

                    trans.Commit();
                    placementCommitted = true;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Transaction failed: {ex.Message}");
                    if (trans.HasStarted())
                        trans.RollBack();
                }
            }

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
                        wireTx.Commit();
                        if (wired)
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
