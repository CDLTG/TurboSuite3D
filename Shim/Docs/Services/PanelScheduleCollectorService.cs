#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Docs.Models;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Docs.Services;

public static class PanelScheduleCollectorService
{
    public static PanelScheduleData Collect(Document doc)
    {
        var collector = new ZonesCollectorService();
        var circuits = collector.GetCircuits(doc);

        var panelSettings = ZonesPanelSettingsStorageService.Load(doc);
        string brandName = panelSettings?.Brand ?? "Lutron";
        var brand = string.Equals(brandName, "Crestron", StringComparison.OrdinalIgnoreCase)
            ? BrandConfig.Crestron
            : BrandConfig.CreateLutron(panelSettings?.UseDedicatedRelayModule ?? false);

        var overrides = panelSettings?.PanelSizeOverrides;

        var (allocation, _) = PanelAllocationService.BuildPanelBreakdown(circuits, brand, overrides);

        // Build lookup keyed by circuit number for wattage/load name resolution.
        // Circuit numbers are unique across Revit electrical systems.
        var lookup = new Dictionary<string, ZonesCircuitData>(StringComparer.OrdinalIgnoreCase);
        foreach (var circuit in circuits)
        {
            lookup.TryAdd(circuit.CircuitNumber, circuit);
        }

        return new PanelScheduleData
        {
            Allocation = allocation,
            CircuitLookup = lookup,
            Brand = brand
        };
    }
}
