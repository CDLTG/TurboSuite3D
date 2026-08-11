#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Turns the job's shade locations into a <see cref="ControlSubsystemDemand"/>: the recommended
    /// QSPS-10PNL count, and the QS-link device/leg budget the shades consume. The second concrete
    /// subsystem after DMX — pure, so the shim only reads the model and hands over the per-location
    /// tallies.
    ///
    /// <b>A recommendation, exactly like the lighting panels.</b> TurboZones does not read placed shade
    /// hardware; it reads the shade circuits and recommends how many panels the job needs. Physical
    /// QSPS-10PNL are recommended <b>per location</b> — a location's shades ceil up to whole ten-output
    /// panels, and the locations are summed. 33 shades in SHADE 1 is four panels; 4 in SHADE 2 is one;
    /// the job needs five. The by-location ceil is what makes the designer's split cost what it costs: a
    /// lone shade wired to its own location is a whole panel, which is why they group shades by proximity
    /// to a processor rather than filling panels blindly.
    ///
    /// <b>Link accounting (085335 + confirmed with the designer).</b> A shade is <b>1 QS device AND 1
    /// switch leg</b>; each recommended QSPS-10PNL is <b>1 more device</b>. Its Link terminals join the
    /// processor link with COM/MUX/MUX — no V+ — so it draws <b>0 PDU</b>, powering its own outputs from
    /// its mains supply (shades never touch the QSPS-DH-1-75). A full panel is 11 devices / 10 legs. The
    /// QSPS-10PNL part is <see cref="DemandMount.External"/>: ordered, but competing for no compartment.
    /// </summary>
    public static class ShadeSolver
    {
        /// <summary>Outputs on a QSPS-10PNL — one shade motor each; the divisor for the per-location ceil.</summary>
        public const int ShadesPerPanel = 10;

        /// <summary>The Sivoia QS Smart Panel catalog number, ordered and described on the BOM.</summary>
        public const string PanelPartNumber = "QSPS-10PNL";

        /// <summary>The subsystem name the BOM and the panel breakdown label this demand with.</summary>
        public const string SubsystemName = "Shades";

        public static ControlSubsystemDemand Solve(IReadOnlyList<ShadeLocationTally>? locations)
        {
            if (locations == null || locations.Count == 0)
                return ControlSubsystemDemand.None(SubsystemName);

            int totalShades = locations.Sum(l => l.ShadeCount);
            if (totalShades == 0)
                return ControlSubsystemDemand.None(SubsystemName);

            // Recommended per location, then summed — never ceil of the grand total, which would let a
            // stray shade in one location absorb into another location's slack it can't physically share.
            int recommendedPanels = locations.Sum(l => CeilDiv(l.ShadeCount, ShadesPerPanel));

            return new ControlSubsystemDemand(
                SubsystemName,
                parts: new List<DemandPart>
                {
                    new DemandPart(PanelPartNumber, recommendedPanels, DemandMount.External)
                },
                linkDevices: totalShades + recommendedPanels,
                linkLoads: totalShades);
        }

        private static int CeilDiv(int n, int d) => (n + d - 1) / d;   // n, d ≥ 0
    }
}
