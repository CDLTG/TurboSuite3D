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
    /// Result of a Warp deployment execution.
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
        public List<ElementId> OverriddenElementIds { get; set; } = new List<ElementId>();
    }

    /// <summary>
    /// Orchestrates TurboDriver deployment.
    /// User picks ONE point or selects an existing power supply; all new power supplies are placed in a column, 9" apart.
    /// </summary>
    public class DeploymentExecutor
    {
        private const double SpacingFt = 9.0 / 12.0; // 9 inches in feet

        /// <summary>
        /// Execute the Warp deployment: pick one point, place all power supplies in a column.
        /// </summary>
        public DeploymentResult Execute(UIDocument uidoc, DeploymentPlan plan)
        {
            Document doc = uidoc.Document;
            var service = new DeploymentService(doc);
            var result = new DeploymentResult();

            // Pick origin: select an existing power supply (new ones placed 9" below)
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
                    origin = uidoc.Selection.PickPoint(
                        $"Pick origin for {plan.TotalQuantity} power supplies");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    result.WasCancelled = true;
                    return result;
                }
            }

            // Place all power supplies in a single transaction
            int globalIndex = 0;

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
                            // Column layout: each instance offset downward (-Y) by 9"
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

                        // Wire consecutive power supplies in this circuit
                        for (int w = 1; w < circuitInstances.Count; w++)
                        {
                            bool wired = service.CreateWireBetween(
                                circuitInstances[w - 1], circuitInstances[w], doc.ActiveView);
                            if (wired)
                                result.TotalWiresPlaced++;
                        }
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Transaction failed: {ex.Message}");
                    if (trans.HasStarted())
                        trans.RollBack();
                }
            }

            // Apply color overrides to show fixture-to-driver assignments
            if (result.PlacedInstanceIds.Count > 0
                && plan.Circuits.Exists(c => c.Assignments != null && c.Assignments.Count > 0))
            {
                using (Transaction visTrans = new Transaction(doc, "TurboDriver — Visual Feedback"))
                {
                    visTrans.Start();
                    result.OverriddenElementIds = VisualFeedbackService.ApplyOverrides(
                        doc.ActiveView, plan.Circuits, result.PlacedInstanceIds);
                    visTrans.Commit();
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
