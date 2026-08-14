#nullable disable
using System;
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// Revit-free snapshot of the Panel Breakdown tab's persisted state (brand, dedicated-relay
    /// toggle, per-panel special-device picks, per-panel size overrides). Built/consumed by the
    /// Core <c>PanelBreakdownTabViewModel</c>; persisted shim-side by
    /// <c>ZonesPanelSettingsStorageService</c> via <see cref="Services.IPanelSettingsStore"/>.
    /// </summary>
    public class PanelSettings
    {
        public string Brand { get; set; }
        public bool UseDedicatedRelayModule { get; set; }

        /// <summary>Lutron only: pack RELAY and 0-10V loads onto the same LQSE-4T5 module instead of
        /// splitting them, to reclaim otherwise-wasted spare slots. Mutually exclusive with
        /// <see cref="UseDedicatedRelayModule"/> (the LQSE-4S8 is a physically different module); the
        /// allocator ignores this flag whenever the two dimming types don't resolve to one part number.</summary>
        public bool AllowRelayZeroTenPacking { get; set; }
        public Dictionary<string, string> SpecialDeviceSelections { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> PanelSizeOverrides { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
}
