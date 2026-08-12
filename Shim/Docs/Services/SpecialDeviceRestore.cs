#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Restores the designer's per-panel LV-compartment device choices (saved in TurboZones) onto a freshly
/// built allocation, so both TurboDocs surfaces that re-derive the breakdown — the Control BOM and the
/// Panel Schedule — show the interfaces the designer actually placed, not the "Empty" defaults
/// <c>BuildPanelBreakdown</c> seeds. One helper so the two cannot drift on how a saved selection maps back
/// onto a panel (the "#2" key for the dual LV21 compartment being the easy thing to get subtly different).
/// </summary>
public static class SpecialDeviceRestore
{
    public static void Apply(PanelAllocationResult allocation, IReadOnlyDictionary<string, string> selections)
    {
        if (allocation == null || selections == null) return;

        foreach (var panel in allocation.AllPanels)
        {
            if (panel.HasSpecialCompartment
                && selections.TryGetValue(panel.PanelName, out var device)
                && panel.SpecialDeviceOptions != null
                && panel.SpecialDeviceOptions.Contains(device))
            {
                panel.SelectedSpecialDevice = device;
            }

            if (panel.HasDualSpecialCompartment
                && selections.TryGetValue(panel.PanelName + "#2", out var device2)
                && panel.SpecialDeviceOptions != null
                && panel.SpecialDeviceOptions.Contains(device2))
            {
                panel.SelectedSpecialDevice2 = device2;
            }
        }
    }
}
