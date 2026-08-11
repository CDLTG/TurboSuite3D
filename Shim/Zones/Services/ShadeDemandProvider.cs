#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;
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
    /// fixture is a shade motor, matched by family name containing <see cref="ShadeMotorFamilyToken"/>
    /// ("Shade Motor" — catches both the 3D <c>AL_Electrical Fixture_Shade Motor</c> and the 2D
    /// <c>Shade Motor</c>). That is the same signal <c>ZonesCollectorService</c> uses (via
    /// <see cref="IsShadeCircuit"/>) to keep shade circuits out of the lighting zones — a shade motor is
    /// an Electrical Fixture, which that collector would otherwise treat as a lighting load.
    ///
    /// <b>Location — the circuit's panel name.</b> Shades wired to a panel named "SHADE 1" group under
    /// "SHADE 1", exactly as lighting groups by its zone panel; the solver ceils each location's shades
    /// to whole QSPS-10PNL and sums.
    ///
    /// <b>Must not throw</b> (see the interface): every read is wrapped, and a failure becomes an
    /// Unsolvable demand so a half-wired shade job never breaks the BOM.
    /// </summary>
    public sealed class ShadeDemandProvider : IControlSubsystemDemandProvider
    {
        /// <summary>Family-name token identifying a shade motor. A substring, case-insensitive, so it
        /// catches both authored families (<c>AL_Electrical Fixture_Shade Motor</c> and <c>Shade
        /// Motor</c>) and future variants that keep the words.</summary>
        public const string ShadeMotorFamilyToken = "Shade Motor";

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
                int shades = CountShadeMotors(circuit);
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

        /// <summary>A circuit carrying shade motors — the hook <c>ZonesCollectorService</c> uses to drop
        /// shade circuits before their motors become a spurious lighting zone.</summary>
        internal static bool IsShadeCircuit(ElectricalSystem circuit) => CountShadeMotors(circuit) > 0;

        private static int CountShadeMotors(ElectricalSystem circuit)
        {
            if (circuit?.Elements == null) return 0;
            int count = 0;
            foreach (Element el in circuit.Elements)
                if (el is FamilyInstance fi && IsShadeMotor(fi))
                    count++;
            return count;
        }

        internal static bool IsShadeMotor(FamilyInstance fi)
        {
            string family = fi?.Symbol?.Family?.Name ?? string.Empty;
            return family.IndexOf(ShadeMotorFamilyToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string LocationOf(ElectricalSystem circuit)
        {
            string panel = ParameterHelper.GetPanelName(circuit);
            return string.IsNullOrWhiteSpace(panel) ? "(unassigned)" : panel.Trim();
        }
    }
}
