#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Docs.Services;

public class BomData
{
    public List<BomLineItem> Items { get; set; } = new();
    public string BrandName { get; set; } = "";
}

public static class BomCollectorService
{
    public static BomData Collect(Document doc)
    {
        var collector = new ZonesCollectorService();
        var circuits = collector.GetCircuits(doc);

        var panelSettings = ZonesPanelSettingsStorageService.Load(doc);
        string brandName = panelSettings?.Brand ?? "Lutron";
        var brand = string.Equals(brandName, "Crestron", StringComparison.OrdinalIgnoreCase)
            ? BrandConfig.Crestron
            : BrandConfig.CreateLutron(panelSettings?.UseDedicatedRelayModule ?? false);

        var overrides = panelSettings?.PanelSizeOverrides;
        var specialSelections = panelSettings?.SpecialDeviceSelections
            ?? new Dictionary<string, string>();

        var subsystemDemands = SubsystemDemandCollector.Collect(doc);

        var (allocation, _) = PanelAllocationService.BuildPanelBreakdown(
            circuits, brand, overrides, subsystemDemands);
        if (allocation == null)
            return new BomData { BrandName = brandName };

        // Restore special device selections from saved settings — same helper the Panel Schedule uses.
        SpecialDeviceRestore.Apply(allocation, specialSelections);

        var keypadCounts = collector.GetKeypadCounts(doc);
        var hybridRepeaters = collector.GetHybridRepeaters(doc);

        // Same builder the TurboZones window uses, so the issued PDF and the live panel breakdown
        // cannot disagree about what to order. The audience is what differs: this is a purchasing
        // document, so no shortfall commentary and no zero-quantity lines.
        var items = ControlBomBuilder.Build(allocation.AllPanels, brand, new BomExtras
        {
            KeypadCount = keypadCounts.Regular,
            TwoGangKeypadCount = keypadCounts.TwoGang,
            WirelessDeviceCount = keypadCounts.WirelessDevices,
            KeypadTallies = keypadCounts.Tallies,
            HybridRepeaters = hybridRepeaters,
            SubsystemDemands = subsystemDemands,
            Audience = BomAudience.IssuedDocument
        });

        return new BomData { Items = items, BrandName = brandName };
    }
}
