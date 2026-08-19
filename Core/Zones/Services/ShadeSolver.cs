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

            // Split by whether the shade location resolves to a real location number (SHADE 1 → 1, the
            // same parse the panel breakdown groups by). Shades with no SHADE N panel can't be assigned a
            // panel at a location that doesn't exist, so they are NOT counted — they surface as a BOM
            // warning instead, and are likewise dropped from the panel-breakdown display.
            int assignedShades = 0, unassignedShades = 0, recommendedPanels = 0;
            foreach (var l in locations)
            {
                if (l == null || l.ShadeCount <= 0) continue;
                if (PanelAllocationService.ParseLocationNumber(l.LocationName) > 0)
                {
                    assignedShades += l.ShadeCount;
                    recommendedPanels += PanelsForLocation(l.ShadeCount);   // per location, then summed
                }
                else
                {
                    unassignedShades += l.ShadeCount;
                }
            }

            if (assignedShades == 0 && unassignedShades == 0)
                return ControlSubsystemDemand.None(SubsystemName);

            string? diagnostic = unassignedShades == 0 ? null
                : $"{unassignedShades} motor{(unassignedShades == 1 ? "" : "s")} not assigned to a " +
                  "SHADE panel — assign to count panels.";

            // Nothing assignable — warning only, no QSPS-10PNL to order.
            if (recommendedPanels == 0)
                return new ControlSubsystemDemand(SubsystemName, diagnostic: diagnostic);

            // Both the summed order and the Panel Breakdown's per-location tiles derive from
            // PanelsForLocation, so the count drawn can never disagree with the count ordered. The link
            // budget counts only the assigned shades (an unassigned shade rides no known link yet).
            return new ControlSubsystemDemand(
                SubsystemName,
                parts: new List<DemandPart>
                {
                    new DemandPart(PanelPartNumber, recommendedPanels, DemandMount.External)
                },
                linkDevices: assignedShades + recommendedPanels,
                linkLoads: assignedShades,
                diagnostic: diagnostic);
        }

        /// <summary>QSPS-10PNL panels one location needs — its shades ceil'd to whole ten-output panels.
        /// The single per-location count: the BOM sums it (see <see cref="Solve"/>) and the Panel Breakdown
        /// draws exactly this many tiles, so the two can never drift.</summary>
        public static int PanelsForLocation(int shadeCount) => CeilDiv(shadeCount, ShadesPerPanel);

        /// <summary>How the shades in one location fill its recommended panels, front-loaded: full tens,
        /// then the remainder on the last panel (33 → 10, 10, 10, 3). The count equals
        /// <see cref="PanelsForLocation"/> and the sum equals <paramref name="shadeCount"/>, so the tiles
        /// the visualizer draws total exactly what the BOM orders. Empty for a zero-shade location.</summary>
        public static IReadOnlyList<int> PanelFills(int shadeCount)
        {
            int panels = PanelsForLocation(shadeCount);
            var fills = new List<int>(panels);
            int remaining = shadeCount;
            for (int i = 0; i < panels; i++)
            {
                int f = remaining < ShadesPerPanel ? remaining : ShadesPerPanel;
                fills.Add(f);
                remaining -= f;
            }
            return fills;
        }

        private static int CeilDiv(int n, int d) => (n + d - 1) / d;   // n, d ≥ 0
    }
}
