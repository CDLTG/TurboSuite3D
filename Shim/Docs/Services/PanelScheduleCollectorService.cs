#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Services;
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

        // Re-derive the allocation exactly as the TurboZones Panel Breakdown does, or the schedule renders a
        // different panel than the one the designer sized. That means both control-subsystem inputs:
        //   • subsystemDemands — so subsystem-owned circuits (DMX) are excluded here as they are there;
        //   • daliModulesByZone — the placed DALI DIN modules, which are the whole reason a schedule can be
        //     one module short (they are placed from this map, never derived from circuits).
        var subsystemDemands = SubsystemDemandCollector.Collect(doc);
        var daliModulesByZone = DaliPlacementMapper.Build(
            DaliStorageService.Load(doc).Loops,
            DaliDemandProvider.CountDaliLoadsByZone(doc, out _)).ByZone;

        var (allocation, _) = PanelAllocationService.BuildPanelBreakdown(
            circuits, brand, overrides, subsystemDemands, daliModulesByZone);

        // Restore the designer's LV-compartment device choices so the schedule's LV block shows the
        // interfaces they actually placed, not the "Empty" defaults — same helper the Control BOM uses.
        SpecialDeviceRestore.Apply(allocation,
            panelSettings?.SpecialDeviceSelections ?? new Dictionary<string, string>());

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
