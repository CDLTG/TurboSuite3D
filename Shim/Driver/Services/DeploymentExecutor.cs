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

            // The plane's level can be a real level OR one of Revit's "Level Above"/"Level Below"
            // sentinel ids (e.g. an RCP with Top = Level Above, offset 0). Sentinels don't resolve
            // via GetElement, so map them to the actual neighbouring level by elevation; otherwise
            // we'd fall back to the picked Z and the device could land out of range.
            ElementId levelId = range.GetLevelId(whichPlane);
            Level level;
            if (levelId.Equals(PlanViewRange.LevelAbove))
                level = AdjacentLevel(doc, plan.GenLevel, above: true);
            else if (levelId.Equals(PlanViewRange.LevelBelow))
                level = AdjacentLevel(doc, plan.GenLevel, above: false);
            else
                level = doc.GetElement(levelId) as Level;

            if (level == null)
                return null;

            return level.Elevation + range.GetOffset(whichPlane);
        }

        /// <summary>
        /// The nearest level directly above (or below) the given level by elevation, or null if
        /// there is none. Used to resolve view-range "Level Above"/"Level Below" sentinels.
        /// </summary>
        private static Level AdjacentLevel(Document doc, Level from, bool above)
        {
            if (from == null)
                return null;

            double baseElev = from.Elevation;
            const double Tol = 1e-6;
            Level best = null;
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                if (!(el is Level lvl))
                    continue;
                double e = lvl.Elevation;
                if (above)
                {
                    if (e > baseElev + Tol && (best == null || e < best.Elevation))
                        best = lvl;
                }
                else
                {
                    if (e < baseElev - Tol && (best == null || e > best.Elevation))
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
                origin = new XYZ(anchorLocation.X, anchorLocation.Y - SpacingFt, anchorLocation.Z);
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
                            // Column layout: each instance offset downward (-Y) by 9.5"
                            XYZ point = new XYZ(origin.X, origin.Y - (globalIndex * SpacingFt), origin.Z);

                            var instance = service.PlacePowerSupply(point, circuit.DriverSymbol);
                            if (instance == null)
                            {
                                result.TotalFailed++;
                                result.Warnings.Add($"Circuit {circuit.CircuitNumber}: Failed to place instance.");
                                globalIndex++;
                                continue;
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
