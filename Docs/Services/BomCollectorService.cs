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
            : BrandConfig.Lutron;

        var overrides = panelSettings?.PanelSizeOverrides;
        var specialSelections = panelSettings?.SpecialDeviceSelections
            ?? new Dictionary<string, string>();

        var (allocation, _) = PanelAllocationService.BuildPanelBreakdown(circuits, brand, overrides);
        if (allocation == null)
            return new BomData { BrandName = brandName };

        // Restore special device selections from saved settings
        foreach (var panel in allocation.AllPanels)
        {
            if (panel.HasSpecialCompartment
                && specialSelections.TryGetValue(panel.PanelName, out var device)
                && panel.SpecialDeviceOptions != null
                && panel.SpecialDeviceOptions.Contains(device))
            {
                panel.SelectedSpecialDevice = device;
            }

            if (panel.HasDualSpecialCompartment
                && specialSelections.TryGetValue(panel.PanelName + "#2", out var device2)
                && panel.SpecialDeviceOptions != null
                && panel.SpecialDeviceOptions.Contains(device2))
            {
                panel.SelectedSpecialDevice2 = device2;
            }
        }

        var (keypadCount, twoGangKeypadCount) = collector.GetKeypadCounts(doc);
        var (hybridRepeaterCount, hybridRepeaterPartNumber) = collector.GetHybridRepeaterInfo(doc);

        var items = BuildBom(allocation.AllPanels, brand,
            keypadCount, twoGangKeypadCount,
            hybridRepeaterCount, hybridRepeaterPartNumber);

        return new BomData { Items = items, BrandName = brandName };
    }

    private static List<BomLineItem> BuildBom(
        List<PanelResult> allPanels, BrandConfig brand,
        int keypadCount, int twoGangKeypadCount,
        int hybridRepeaterCount, string hybridRepeaterPartNumber)
    {
        var bom = new List<BomLineItem>();

        // --- Processors ---
        int processorCount = 0;
        foreach (var panel in allPanels)
        {
            if (!panel.HasSpecialCompartment) continue;
            if (string.Equals(panel.SelectedSpecialDevice, "Processor", StringComparison.OrdinalIgnoreCase))
                processorCount++;
            if (panel.HasDualSpecialCompartment
                && string.Equals(panel.SelectedSpecialDevice2, "Processor", StringComparison.OrdinalIgnoreCase))
                processorCount++;
        }

        int recommendedProcessors = CalculateRecommendedProcessors(
            allPanels, brand, keypadCount, twoGangKeypadCount, hybridRepeaterCount);
        int bomProcessorCount = Math.Max(recommendedProcessors, processorCount);

        {
            bom.Add(new BomLineItem { IsHeader = true, Category = "Processors", Description = "Processors" });

            string processorPn = brand.SpecialDevices != null
                && brand.SpecialDevices.TryGetValue("Processor", out var ppn) ? ppn : "";
            string description = brand.GetPartDescription(processorPn);

            bom.Add(new BomLineItem
            {
                Quantity = bomProcessorCount,
                PartNumber = processorPn,
                Description = description,
                Category = "Processors"
            });
        }

        // --- Panels ---
        var panelsBySize = allPanels.GroupBy(p => p.PanelCapacity).OrderByDescending(g => g.Key).ToList();
        if (panelsBySize.Count > 0)
        {
            bom.Add(new BomLineItem { IsHeader = true, Category = "Panels", Description = "Panels" });

            foreach (var group in panelsBySize)
            {
                string partNumber = brand.PanelPartNumbers.TryGetValue(group.Key, out var pn) ? pn : "";
                bom.Add(new BomLineItem
                {
                    Quantity = group.Count(),
                    PartNumber = partNumber,
                    Description = brand.GetPartDescription(partNumber),
                    Category = "Panels"
                });
            }
        }

        // --- Modules ---
        var allModules = allPanels.SelectMany(p => p.Modules).ToList();
        if (allModules.Count > 0)
        {
            bom.Add(new BomLineItem { IsHeader = true, Category = "Modules", Description = "Modules" });

            var modulesByType = allModules.GroupBy(m => m.DimmingType).ToList();
            foreach (var typeGroup in PanelAllocationService.ModuleTypeOrder)
            {
                var group = modulesByType.FirstOrDefault(g =>
                    string.Equals(g.Key, typeGroup, StringComparison.OrdinalIgnoreCase));
                if (group == null) continue;
                string modulePn = brand.GetModulePartNumber(group.Key);
                bom.Add(new BomLineItem
                {
                    Quantity = group.Count(),
                    PartNumber = modulePn,
                    Description = brand.GetPartDescription(modulePn),
                    Category = "Modules"
                });
            }
            // Non-standard dimming types
            foreach (var group in modulesByType)
            {
                bool isStandard = false;
                foreach (var t in PanelAllocationService.ModuleTypeOrder)
                {
                    if (string.Equals(group.Key, t, StringComparison.OrdinalIgnoreCase))
                    { isStandard = true; break; }
                }
                if (!isStandard)
                {
                    string modulePn = brand.GetModulePartNumber(group.Key);
                    bom.Add(new BomLineItem
                    {
                        Quantity = group.Count(),
                        PartNumber = modulePn,
                        Description = brand.GetPartDescription(modulePn),
                        Category = "Modules"
                    });
                }
            }
        }

        // --- Accessories ---
        var accessories = new List<BomLineItem>();

        // Power supply: 1 per processor
        if (!string.IsNullOrEmpty(brand.PowerSupplyPartNumber))
        {
            accessories.Add(new BomLineItem
            {
                Quantity = bomProcessorCount,
                PartNumber = brand.PowerSupplyPartNumber,
                Description = brand.GetPartDescription(brand.PowerSupplyPartNumber),
                Category = "Accessories"
            });
        }

        // Wire harnesses (one per panel, grouped by part number)
        if (brand.WireHarnessPartNumbers != null)
        {
            var harnessCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in panelsBySize)
            {
                if (brand.WireHarnessPartNumbers.TryGetValue(group.Key, out var harnessPn))
                {
                    if (!harnessCounts.ContainsKey(harnessPn))
                        harnessCounts[harnessPn] = 0;
                    harnessCounts[harnessPn] += group.Count();
                }
            }

            foreach (var kvp in harnessCounts)
            {
                accessories.Add(new BomLineItem
                {
                    Quantity = kvp.Value,
                    PartNumber = kvp.Key,
                    Description = brand.GetPartDescription(kvp.Key),
                    Category = "Accessories"
                });
            }
        }

        // Special devices (Digital I/O, DMX — excludes Processor and Empty)
        if (brand.SpecialDevices != null)
        {
            var specialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var panel in allPanels)
            {
                if (!panel.HasSpecialCompartment) continue;

                var slots = new List<string> { panel.SelectedSpecialDevice };
                if (panel.HasDualSpecialCompartment)
                    slots.Add(panel.SelectedSpecialDevice2);

                foreach (string selected in slots)
                {
                    if (string.IsNullOrEmpty(selected)
                        || string.Equals(selected, "Empty", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(selected, "Processor", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!specialCounts.ContainsKey(selected))
                        specialCounts[selected] = 0;
                    specialCounts[selected]++;
                }
            }

            foreach (var kvp in specialCounts)
            {
                string partNumber = brand.SpecialDevices.TryGetValue(kvp.Key, out var spn) ? spn : "";
                accessories.Add(new BomLineItem
                {
                    Quantity = kvp.Value,
                    PartNumber = partNumber,
                    Description = brand.GetPartDescription(partNumber),
                    Category = "Accessories"
                });
            }
        }

        // Hybrid repeaters
        if (hybridRepeaterCount > 0
            && string.Equals(brand.Name, "Lutron", StringComparison.OrdinalIgnoreCase))
        {
            accessories.Add(new BomLineItem
            {
                Quantity = hybridRepeaterCount,
                PartNumber = hybridRepeaterPartNumber ?? "",
                Description = "HWQS Hybrid Wired/Wireless RF System Repeater",
                Category = "Accessories"
            });
        }

        if (accessories.Count > 0)
        {
            bom.Add(new BomLineItem { IsHeader = true, Category = "Accessories", Description = "Accessories" });
            bom.AddRange(accessories);
        }

        // --- Keypads ---
        if (keypadCount > 0 || twoGangKeypadCount > 0)
        {
            bom.Add(new BomLineItem { IsHeader = true, Category = "Keypads", Description = "Keypads" });
            if (keypadCount > 0)
            {
                bom.Add(new BomLineItem
                {
                    Quantity = keypadCount,
                    PartNumber = "",
                    Description = "Keypad",
                    Category = "Keypads"
                });
            }
            if (twoGangKeypadCount > 0)
            {
                bom.Add(new BomLineItem
                {
                    Quantity = twoGangKeypadCount,
                    PartNumber = "",
                    Description = "Two-Gang Keypad",
                    Category = "Keypads"
                });
            }
        }

        return bom;
    }

    private static int CalculateRecommendedProcessors(
        List<PanelResult> allPanels, BrandConfig brand,
        int keypadCount, int twoGangKeypadCount, int hybridRepeaterCount)
    {
        int specialDeviceCount = 0;
        foreach (var panel in allPanels)
        {
            if (!panel.HasSpecialCompartment) continue;

            var slots = new List<string> { panel.SelectedSpecialDevice };
            if (panel.HasDualSpecialCompartment)
                slots.Add(panel.SelectedSpecialDevice2);

            foreach (string selected in slots)
            {
                if (string.Equals(selected, "Digital I/O", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(selected, "DMX", StringComparison.OrdinalIgnoreCase))
                    specialDeviceCount++;
            }
        }

        int totalDevices = allPanels.Sum(p => p.DeviceCount)
            + keypadCount + twoGangKeypadCount * 2
            + specialDeviceCount;
        int totalLoads = allPanels.Sum(p => p.LoadCount);

        int qsLinksNeeded = Math.Max(
            (int)Math.Ceiling((double)totalDevices / ProcessorLink.MaxDevices),
            (int)Math.Ceiling((double)totalLoads / ProcessorLink.MaxLoads));
        qsLinksNeeded = Math.Max(qsLinksNeeded, 1);

        int ccaLinksNeeded = hybridRepeaterCount > 0
            ? Math.Max(1, (int)Math.Ceiling((double)hybridRepeaterCount / ProcessorLink.MaxDevices))
            : 0;

        int totalLinksNeeded = qsLinksNeeded + ccaLinksNeeded;
        return Math.Max(1, (int)Math.Ceiling((double)totalLinksNeeded / 2));
    }
}
