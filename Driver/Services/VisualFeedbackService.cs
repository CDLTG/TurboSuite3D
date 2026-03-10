#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Applies per-view color overrides to lighting fixtures and power supplies
    /// to indicate which fixtures are assigned to which driver.
    /// </summary>
    public static class VisualFeedbackService
    {
        private static readonly Color[] Palette =
        {
            new Color(220, 40, 40),    // Red
            new Color(0, 90, 220),     // Blue
            new Color(0, 160, 0),      // Green
            new Color(200, 120, 0),    // Orange
            new Color(160, 0, 180),    // Purple
            new Color(0, 170, 170),    // Teal
            new Color(180, 0, 90),     // Magenta
            new Color(100, 100, 100),  // Gray
        };

        // Static fields for auto-clear on next run
        private static List<ElementId> _previousOverriddenIds;
        private static ElementId _previousViewId;

        /// <summary>
        /// Apply color overrides to fixtures and power supplies based on driver assignments.
        /// Must be called inside an active Transaction.
        /// </summary>
        public static List<ElementId> ApplyOverrides(
            View view,
            List<CircuitDeployment> circuits,
            List<ElementId> placedInstanceIds)
        {
            var overriddenIds = new List<ElementId>();
            int globalPlacementIndex = 0;

            foreach (var circuit in circuits)
            {
                if (circuit.Assignments == null || circuit.Assignments.Count == 0)
                {
                    globalPlacementIndex += circuit.QuantityToPlace;
                    continue;
                }

                // Group assignments by DriverIndex to map to placed power supplies
                var byDriver = circuit.Assignments
                    .GroupBy(a => a.DriverIndex)
                    .OrderBy(g => g.Key)
                    .ToList();

                // Track the base placement index for this circuit
                int circuitBaseIndex = globalPlacementIndex;

                foreach (var driverGroup in byDriver)
                {
                    int driverIndex = driverGroup.Key; // 1-based
                    int placementIndex = circuitBaseIndex + driverIndex - 1;
                    Color color = Palette[(driverIndex - 1) % Palette.Length];

                    var ogs = CreateOverride(color);

                    // Color the placed power supply
                    if (placementIndex < placedInstanceIds.Count)
                    {
                        var deviceId = placedInstanceIds[placementIndex];
                        view.SetElementOverrides(deviceId, ogs);
                        overriddenIds.Add(deviceId);
                    }

                    // Color all fixtures assigned to this driver
                    foreach (var assignment in driverGroup)
                    {
                        foreach (var segment in assignment.Segments)
                        {
                            if (segment.FixtureId != null && segment.FixtureId != ElementId.InvalidElementId)
                            {
                                view.SetElementOverrides(segment.FixtureId, ogs);
                                if (!overriddenIds.Contains(segment.FixtureId))
                                    overriddenIds.Add(segment.FixtureId);
                            }
                        }
                    }
                }

                globalPlacementIndex += circuit.QuantityToPlace;
            }

            // Store for auto-clear on next run
            _previousOverriddenIds = new List<ElementId>(overriddenIds);
            _previousViewId = view.Id;

            return overriddenIds;
        }

        /// <summary>
        /// Clear color overrides from a previous TurboDriver run.
        /// Call at the start of DriverCommand.Execute before any circuit work.
        /// </summary>
        public static void ClearPreviousOverrides(Document doc)
        {
            if (_previousOverriddenIds == null || _previousOverriddenIds.Count == 0)
                return;

            var view = doc.GetElement(_previousViewId) as View;
            if (view == null)
            {
                _previousOverriddenIds = null;
                _previousViewId = null;
                return;
            }

            using (Transaction t = new Transaction(doc, "TurboDriver — Clear color overrides"))
            {
                t.Start();
                var blank = new OverrideGraphicSettings();
                foreach (var id in _previousOverriddenIds)
                {
                    if (doc.GetElement(id) != null)
                        view.SetElementOverrides(id, blank);
                }
                t.Commit();
            }

            _previousOverriddenIds = null;
            _previousViewId = null;
        }

        private static OverrideGraphicSettings CreateOverride(Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetProjectionLineWeight(6);
            ogs.SetProjectionLinePatternId(LinePatternElement.GetSolidPatternId());
            ogs.SetSurfaceBackgroundPatternColor(color);
            ogs.SetCutBackgroundPatternColor(color);
            return ogs;
        }
    }
}
