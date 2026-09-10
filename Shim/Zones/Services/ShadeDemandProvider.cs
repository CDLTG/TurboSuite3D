#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Sivoia QS shades as a control-subsystem demand provider — the second after DMX. Reads the shade
    /// circuits, groups them by location, and hands the per-location tallies to the pure
    /// <see cref="ShadeSolver"/>, which recommends the QSPS-10PNL count. TurboZones never reads placed
    /// shade panels: like the lighting panels, the panel count is a recommendation off the circuits.
    ///
    /// <b>Identity — the shade motor, not the panel.</b> A circuit is a shade circuit when a connected
    /// fixture is a shade motor (see <see cref="ShadeCircuitClassifier"/>, the shared identity). That is
    /// the same signal <c>ZonesCollectorService</c> uses to keep shade circuits out of the lighting
    /// zones — a shade motor is an Electrical Fixture, which that collector would otherwise treat as a
    /// lighting load.
    ///
    /// <b>Location — the circuit's panel name.</b> The shade panel follows the "{Location}-{Panel ID}"
    /// convention (e.g. "2-D") and groups by that name, exactly as lighting groups by its panel; the
    /// parsed location number (<see cref="PanelAllocationService.ParseLocationNumber"/>, dash path)
    /// merges it into the matching lighting location. The older "SHADE N" form still resolves the same.
    /// The solver ceils each location's shades to whole QSPS-10PNL and sums. This assumes one
    /// shade-panel name per location (all its shade circuits on the one real panel — the extras are
    /// circuitless dummies): grouping by name then equals grouping by location, so the per-location
    /// ceil is exact.
    ///
    /// <b>Must not throw</b> (see the interface): every read is wrapped, and a failure becomes an
    /// Unsolvable demand so a half-wired shade job never breaks the BOM.
    /// </summary>
    public sealed class ShadeDemandProvider : IControlSubsystemDemandProvider
    {
        private readonly Document _doc;

        public ShadeDemandProvider(Document doc) => _doc = doc;

        public ControlSubsystemDemand GetDemand()
        {
            if (_doc == null) return ControlSubsystemDemand.None(ShadeSolver.SubsystemName);
            try
            {
                return ShadeSolver.Solve(CollectLocations(_doc));
            }
            catch (Exception ex)
            {
                return ControlSubsystemDemand.Unsolvable(
                    ShadeSolver.SubsystemName, "could not read shade circuits — " + ex.Message);
            }
        }

        /// <summary>Shade motors totalled per location (circuit panel name), in first-seen order.</summary>
        internal static List<ShadeLocationTally> CollectLocations(Document doc)
        {
            var byLocation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>();

            foreach (var circuit in circuits)
            {
                int shades = ShadeCircuitClassifier.CountShadeMotors(circuit);
                if (shades == 0) continue;

                string location = LocationOf(circuit);
                if (!byLocation.ContainsKey(location))
                {
                    byLocation[location] = 0;
                    order.Add(location);
                }
                byLocation[location] += shades;
            }

            return order.Select(loc => new ShadeLocationTally(loc, byLocation[loc])).ToList();
        }

        private static string LocationOf(ElectricalSystem circuit)
        {
            string panel = ParameterHelper.GetPanelName(circuit);
            return string.IsNullOrWhiteSpace(panel) ? "(unassigned)" : panel.Trim();
        }
    }
}
