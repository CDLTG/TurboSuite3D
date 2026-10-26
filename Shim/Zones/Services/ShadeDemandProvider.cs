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
    /// <b>Location — parsed from the circuit's panel name.</b> The shade panel follows the
    /// "{Location}-{Panel ID}" convention (e.g. "2-D"; the older "SHADE N" form resolves the same), and
    /// its parsed location number (<see cref="PanelAllocationService.ParseLocationNumber"/>) merges it
    /// into the matching lighting location. Shades pool by that NUMBER, not the panel name — exactly as
    /// lighting pools every panel in a location — via the pure <see cref="ShadeSolver.PoolByLocation"/>,
    /// which keeps this counts path in step with the panel schedule's circuits path (it pools by
    /// location too).
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

        /// <summary>Shade motors totalled per location (parsed from the circuit panel name), in
        /// first-seen order — one raw tally per circuit, pooled by <see cref="ShadeSolver.PoolByLocation"/>.</summary>
        internal static List<ShadeLocationTally> CollectLocations(Document doc)
        {
            var perCircuit = new List<ShadeLocationTally>();

            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>();

            foreach (var circuit in circuits)
            {
                int shades = ShadeCircuitClassifier.CountShadeMotors(circuit);
                if (shades == 0) continue;

                // Per-motor records for the one-line's motor list (Phase E): circuit # + its load name,
                // repeated per motor so the record count tracks the shade count (one output = one motor
                // by convention ⇒ one record normally).
                string circuitNumber = ParameterHelper.GetCircuitNumber(circuit) ?? string.Empty;
                string loadName = ParameterHelper.GetLoadName(circuit) ?? string.Empty;
                var motors = Enumerable.Repeat(new ShadeMotorRecord(circuitNumber, loadName), shades).ToList();
                perCircuit.Add(new ShadeLocationTally(LocationOf(circuit), shades, motors));
            }

            return ShadeSolver.PoolByLocation(perCircuit);
        }

        private static string LocationOf(ElectricalSystem circuit)
        {
            string panel = ParameterHelper.GetPanelName(circuit);
            return string.IsNullOrWhiteSpace(panel) ? "(unassigned)" : panel.Trim();
        }
    }
}
