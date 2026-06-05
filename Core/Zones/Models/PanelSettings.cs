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
        public Dictionary<string, string> SpecialDeviceSelections { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> PanelSizeOverrides { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
}
