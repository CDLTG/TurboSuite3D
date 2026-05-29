#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public static class PanelAllocationService
    {
        private const double SparePercentage = 0.05;

        // Module ordering inside panels: Relay first, then 0-10V, then ELV
        internal static readonly string[] ModuleTypeOrder = { "Relay", "0-10V", "ELV" };

        /// <summary>
        /// Groups circuits by zone (ZONE N panels), recommends the minimum number of
        /// real panels per zone, and distributes modules across them.
        /// Circuits on "DUMMY" panels are excluded. Circuits without a panel are unassigned.
        /// </summary>
        public static (PanelAllocationResult Result, List<ZonesCircuitData> Unassigned) BuildPanelBreakdown(
            List<ZonesCircuitData> circuits,
            BrandConfig brand,
            Dictionary<string, int> panelSizeOverrides = null)
        {
            var unassigned = new List<ZonesCircuitData>();

            // Group circuits by zone number, filtering out DUMMY and unassigned
            var circuitsByZone = new Dictionary<int, List<ZonesCircuitData>>();
            foreach (var circuit in circuits)
            {
                if (string.IsNullOrWhiteSpace(circuit.PanelName))
                {
                    // Switch-wired circuits are legitimately unpaneled — only warn for truly unassigned
                    if (!circuit.IsWiredToSwitch)
                        unassigned.Add(circuit);
                    continue;
                }

                // Skip DUMMY panel
                if (string.Equals(circuit.PanelName, "DUMMY", StringComparison.OrdinalIgnoreCase))
                    continue;

                int zone = ParseLocationNumber(circuit.PanelName);
                if (zone == 0)
                {
                    unassigned.Add(circuit);
                    continue;
                }
                if (!circuitsByZone.ContainsKey(zone))
                    circuitsByZone[zone] = new List<ZonesCircuitData>();
                circuitsByZone[zone].Add(circuit);
            }

            var result = new PanelAllocationResult();

            foreach (var zone in circuitsByZone.Keys.OrderBy(z => z))
            {
                var zoneCircuits = circuitsByZone[zone];

                // Group all circuits in this zone by dimming type
                var circuitsByType = zoneCircuits
                    .GroupBy(c => c.DimmingType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CircuitNumber).ToList(), StringComparer.OrdinalIgnoreCase);

                // Calculate modules needed per dimming type
                var moduleCountByType = new Dictionary<string, int>();
                foreach (var kvp in circuitsByType)
                {
                    int moduleCap = brand.GetModuleCapacity(kvp.Key);
                    int modules = CalculateModuleCount(kvp.Value.Count, moduleCap);
                    var ampLimits = brand.GetAmpLimitsForDimmingType(kvp.Key);
                    if (ampLimits != null)
                    {
                        int ampBased = SimulateFfdModuleCount(kvp.Value, moduleCap, ampLimits);
                        modules = Math.Max(modules, ampBased);
                    }
                    moduleCountByType[kvp.Key] = modules;
                }

                int zoneTotalModules = moduleCountByType.Values.Sum();

                // Determine default panel size and recommended panel count
                int defaultSize = brand.DefaultPanelSize;
                int panelCount = zoneTotalModules == 0 ? 1 : (int)Math.Ceiling((double)zoneTotalModules / defaultSize);
                panelCount = Math.Max(panelCount, 1);

                // Generate recommended panel names and apply size overrides
                var panelSizes = new List<(string Name, int Size)>();
                for (int i = 0; i < panelCount; i++)
                {
                    string name = $"{zone}-{(char)('A' + i)}";
                    int size = defaultSize;
                    if (panelSizeOverrides != null && panelSizeOverrides.TryGetValue(name, out int overrideSize))
                        size = overrideSize;
                    panelSizes.Add((name, size));
                }

                // Check if overrides require adding/removing panels
                int totalCapacity = panelSizes.Sum(p => p.Size);
                while (totalCapacity < zoneTotalModules)
                {
                    // Add another panel at default size
                    string name = $"{zone}-{(char)('A' + panelSizes.Count)}";
                    int size = defaultSize;
                    if (panelSizeOverrides != null && panelSizeOverrides.TryGetValue(name, out int overrideSize))
                        size = overrideSize;
                    panelSizes.Add((name, size));
                    totalCapacity += size;
                }

                // Remove trailing empty panels (but keep at least 1)
                while (panelSizes.Count > 1)
                {
                    int withoutLast = panelSizes.Take(panelSizes.Count - 1).Sum(p => p.Size);
                    if (withoutLast >= zoneTotalModules)
                        panelSizes.RemoveAt(panelSizes.Count - 1);
                    else
                        break;
                }

                // Build PanelResults and distribute modules across panels
                var locationResult = new LocationResult
                {
                    LocationNumber = zone
                };

                var panelResults = new List<PanelResult>();
                foreach (var (name, size) in panelSizes)
                {
                    var panelResult = new PanelResult
                    {
                        PanelName = name,
                        SelectedPanelSize = size,
                        SpecialCompartmentPanelSizes = brand.SpecialCompartmentPanelSizes,
                        DualCompartmentPanelSizes = brand.DualCompartmentPanelSizes,
                        AvailablePanelSizes = brand.PanelSizes.OrderBy(s => s)
                            .Select(s => new PanelSizeOption
                            {
                                Size = s,
                                DisplayName = brand.GetPanelDisplayName(s)
                            }).ToList()
                    };

                    // Set up special device options if applicable
                    if (panelResult.HasSpecialCompartment && brand.SpecialDevices != null)
                    {
                        panelResult.SpecialDeviceOptions = new List<string> { "Empty" };
                        panelResult.SpecialDeviceOptions.AddRange(brand.SpecialDevices.Keys);
                        panelResult.SpecialDevicePartNumbers = brand.SpecialDevices;
                        panelResult.SelectedSpecialDevice = "Empty";
                        if (panelResult.HasDualSpecialCompartment)
                            panelResult.SelectedSpecialDevice2 = "Empty";
                    }

                    panelResults.Add(panelResult);
                }

                // Distribute modules across panels evenly
                DistributeModulesAcrossPanels(panelResults, circuitsByType, moduleCountByType, brand);

                foreach (var panel in panelResults)
                {
                    locationResult.Panels.Add(panel);
                    locationResult.TotalModules += panel.TotalModuleCount;
                }

                result.Locations.Add(locationResult);
            }

            return (result, unassigned);
        }

        /// <summary>
        /// Distributes modules of each dimming type evenly across the given panels,
        /// respecting each panel's capacity.
        /// </summary>
        private static void DistributeModulesAcrossPanels(
            List<PanelResult> panels,
            Dictionary<string, List<ZonesCircuitData>> circuitsByType,
            Dictionary<string, int> moduleCountByType,
            BrandConfig brand)
        {
            if (panels.Count == 0) return;

            // Track remaining capacity per panel
            var remainingCapacity = panels.ToDictionary(p => p, p => p.PanelCapacity);

            // Allocate module slots per panel per type
            var allocation = panels.ToDictionary(p => p, _ => new Dictionary<string, int>());

            var orderedTypes = GetOrderedTypes(circuitsByType.Keys).ToList();

            foreach (var type in orderedTypes)
            {
                if (!moduleCountByType.TryGetValue(type, out int totalModulesForType) || totalModulesForType == 0)
                    continue;

                int remaining = totalModulesForType;

                // Spread evenly: assign proportionally based on remaining capacity
                int totalRemaining = remainingCapacity.Values.Sum();
                if (totalRemaining == 0) break;

                foreach (var panel in panels)
                {
                    if (remaining <= 0) break;

                    int cap = remainingCapacity[panel];
                    if (cap <= 0) continue;

                    // Proportional share, at least 1 if there's remaining to assign
                    int share = (int)Math.Ceiling((double)remaining * cap / totalRemaining);
                    share = Math.Min(share, Math.Min(remaining, cap));

                    allocation[panel][type] = share;
                    remainingCapacity[panel] -= share;
                    remaining -= share;
                    totalRemaining -= cap;
                }

                // If any remaining (due to rounding), assign to first panel with capacity
                if (remaining > 0)
                {
                    foreach (var panel in panels)
                    {
                        if (remaining <= 0) break;
                        int cap = remainingCapacity[panel];
                        int give = Math.Min(remaining, cap);
                        if (give > 0)
                        {
                            allocation[panel][type] = allocation[panel].GetValueOrDefault(type) + give;
                            remainingCapacity[panel] -= give;
                            remaining -= give;
                        }
                    }
                }
            }

            // Build modules for each panel using its allocated counts.
            // Track how many circuits of each type have been assigned to previous panels
            // so each panel gets the next slice of that type's circuit list.
            var circuitOffsetByType = new Dictionary<string, int>();

            foreach (var panel in panels)
            {
                var panelAlloc = allocation[panel];

                foreach (var type in orderedTypes)
                {
                    if (!panelAlloc.TryGetValue(type, out int moduleCount) || moduleCount == 0)
                        continue;

                    if (!circuitsByType.TryGetValue(type, out var typeCircuits))
                        continue;

                    int moduleCapacity = brand.GetModuleCapacity(type);
                    int offset = circuitOffsetByType.GetValueOrDefault(type);

                    // Calculate how many circuits go on this panel's modules for this type
                    int totalModulesForType = moduleCountByType[type];
                    int totalCircuitsForType = typeCircuits.Count;

                    // Proportional circuit share for this panel
                    int circuitsForPanel = (int)Math.Ceiling((double)totalCircuitsForType * moduleCount / totalModulesForType);
                    circuitsForPanel = Math.Min(circuitsForPanel, moduleCount * moduleCapacity);
                    circuitsForPanel = Math.Min(circuitsForPanel, totalCircuitsForType - offset);

                    var panelCircuits = typeCircuits.Skip(offset).Take(circuitsForPanel).ToList();
                    circuitOffsetByType[type] = offset + panelCircuits.Count;

                    var modules = BuildModules(type, panelCircuits, moduleCount, moduleCapacity, brand);
                    panel.Modules.AddRange(modules);
                }
            }
        }

        /// <summary>
        /// Parses location number from zone panel name.
        /// Supports "ZONE N" format (case-insensitive) and legacy "{number}-{letter}" format.
        /// Returns 0 if the panel name doesn't match any expected format.
        /// </summary>
        internal static int ParseLocationNumber(string panelName)
        {
            if (string.IsNullOrEmpty(panelName))
                return 0;

            // "ZONE N" format (case-insensitive)
            if (panelName.StartsWith("ZONE ", StringComparison.OrdinalIgnoreCase))
            {
                string numPart = panelName.Substring(5).Trim();
                if (int.TryParse(numPart, out int zoneNum))
                    return zoneNum;
            }

            // Legacy "{number}-{letter}" format
            int dashIndex = panelName.IndexOf('-');
            if (dashIndex > 0 && int.TryParse(panelName.Substring(0, dashIndex), out int locNum))
                return locNum;

            return 0;
        }

        internal static int CalculateModuleCount(int circuitCount, int moduleCapacity)
        {
            if (circuitCount == 0) return 0;

            int requiredCapacity = (int)Math.Ceiling(circuitCount * (1.0 + SparePercentage));
            int modules = (int)Math.Ceiling((double)requiredCapacity / moduleCapacity);

            // Don't create a completely empty module
            if ((modules - 1) * moduleCapacity >= circuitCount)
                modules--;

            return Math.Max(modules, 1);
        }

        internal static List<ModuleResult> BuildModules(
            string dimmingType,
            List<ZonesCircuitData> circuits,
            int moduleCount,
            int moduleCapacity,
            BrandConfig brand)
        {
            var limits = brand.GetAmpLimitsForDimmingType(dimmingType);
            if (limits != null)
                return BuildModulesAmpAware(dimmingType, circuits, moduleCount, moduleCapacity, brand, limits);

            return BuildModulesCountBased(dimmingType, circuits, moduleCount, moduleCapacity, brand);
        }

        private static List<ModuleResult> BuildModulesCountBased(
            string dimmingType,
            List<ZonesCircuitData> circuits,
            int moduleCount,
            int moduleCapacity,
            BrandConfig brand)
        {
            var modules = new List<ModuleResult>();
            int circuitIdx = 0;

            for (int m = 0; m < moduleCount; m++)
            {
                var module = new ModuleResult
                {
                    DimmingType = dimmingType,
                    PartNumber = brand.GetModulePartNumber(dimmingType),
                    ModuleCapacity = moduleCapacity
                };

                int remainingModules = moduleCount - m;
                int remainingCircuits = circuits.Count - circuitIdx;
                int circuitsForThisModule = (int)Math.Ceiling((double)remainingCircuits / remainingModules);
                circuitsForThisModule = Math.Min(circuitsForThisModule, moduleCapacity);

                for (int c = 0; c < circuitsForThisModule && circuitIdx < circuits.Count; c++)
                {
                    module.CircuitNumbers.Add(circuits[circuitIdx].CircuitNumber);
                    circuitIdx++;
                }

                modules.Add(module);
            }

            return modules;
        }

        /// <summary>
        /// Amp-aware allocation. Tries circuit-number-order packing first so the natural
        /// reading order is preserved; only falls back to FFD bin-packing when the simple
        /// path produces overloaded modules AND FFD gives a better result (fewer modules,
        /// or fewer overloads). Each bin gets slot-1 promotion for any over-default circuit.
        /// </summary>
        private static List<ModuleResult> BuildModulesAmpAware(
            string dimmingType,
            List<ZonesCircuitData> circuits,
            int moduleCount,
            int moduleCapacity,
            BrandConfig brand,
            ModuleAmpLimits limits)
        {
            string partNumber = brand.GetModulePartNumber(dimmingType);
            double voltage = limits.Voltage <= 0 ? 120.0 : limits.Voltage;

            var withAmps = circuits
                .Select(c => (Circuit: c, Amps: c.ApparentLoadVA / voltage))
                .ToList();

            var sequentialBins = PackSequentialBins(withAmps, moduleCapacity);
            var sequentialModules = BinsToModules(sequentialBins, dimmingType, partNumber, moduleCapacity, limits);
            int sequentialOverloads = sequentialModules.Count(m => m.IsOverloaded);

            List<ModuleResult> chosen = sequentialModules;
            if (sequentialOverloads > 0)
            {
                var ffdBins = PackFfdBins(withAmps, moduleCapacity, limits);
                var ffdModules = BinsToModules(ffdBins, dimmingType, partNumber, moduleCapacity, limits);
                int ffdOverloads = ffdModules.Count(m => m.IsOverloaded);

                // Prefer FFD only when it actually helps — fewer modules, or same module
                // count with fewer overloads. Otherwise keep the readable sequential layout.
                bool ffdBetter = ffdModules.Count < sequentialModules.Count
                    || (ffdModules.Count == sequentialModules.Count && ffdOverloads < sequentialOverloads);
                if (ffdBetter)
                    chosen = ffdModules;
            }

            // Pad with empty modules if the allocator reserved extra capacity (spare %).
            while (chosen.Count < moduleCount)
            {
                chosen.Add(new ModuleResult
                {
                    DimmingType = dimmingType,
                    PartNumber = partNumber,
                    ModuleCapacity = moduleCapacity
                });
            }

            return chosen;
        }

        /// <summary>
        /// Packs circuits in their incoming (circuit-number) order, capping each bin at
        /// the module slot count. Does NOT enforce amp limits — overflows are flagged later.
        /// </summary>
        private static List<List<(ZonesCircuitData Circuit, double Amps)>> PackSequentialBins(
            List<(ZonesCircuitData Circuit, double Amps)> items, int moduleCapacity)
        {
            var bins = new List<List<(ZonesCircuitData Circuit, double Amps)>>();
            for (int i = 0; i < items.Count; i++)
            {
                if (bins.Count == 0 || bins[^1].Count >= moduleCapacity)
                    bins.Add(new List<(ZonesCircuitData Circuit, double Amps)>());
                bins[^1].Add(items[i]);
            }
            return bins;
        }

        /// <summary>
        /// First-fit-decreasing: sort by amps desc, place each in the first bin with both
        /// slot room and amp room. Minimizes module count when wattage is binding.
        /// </summary>
        private static List<List<(ZonesCircuitData Circuit, double Amps)>> PackFfdBins(
            List<(ZonesCircuitData Circuit, double Amps)> items,
            int moduleCapacity, ModuleAmpLimits limits)
        {
            const double Eps = 1e-9;
            var sorted = items.OrderByDescending(x => x.Amps).ToList();
            var bins = new List<List<(ZonesCircuitData Circuit, double Amps)>>();
            foreach (var item in sorted)
            {
                bool placed = false;
                foreach (var bin in bins)
                {
                    double binAmps = 0;
                    for (int i = 0; i < bin.Count; i++) binAmps += bin[i].Amps;
                    if (bin.Count < moduleCapacity
                        && binAmps + item.Amps <= limits.ModuleTotalAmpLimit + Eps)
                    {
                        bin.Add(item);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    bins.Add(new List<(ZonesCircuitData Circuit, double Amps)> { item });
            }
            return bins;
        }

        /// <summary>
        /// Converts bins to <see cref="ModuleResult"/>s with circuit-number order within
        /// each module, slot-1 promotion for any over-default circuit, and overload flag.
        /// </summary>
        private static List<ModuleResult> BinsToModules(
            List<List<(ZonesCircuitData Circuit, double Amps)>> bins,
            string dimmingType, string partNumber, int moduleCapacity, ModuleAmpLimits limits)
        {
            const double Eps = 1e-9;
            var modules = new List<ModuleResult>();
            foreach (var bin in bins)
            {
                var ordered = bin
                    .OrderBy(b => b.Circuit.CircuitNumber, NaturalStringComparer.OrdinalIgnoreCase)
                    .ToList();

                int slot1Idx = -1;
                double slot1Amps = limits.DefaultSlotAmpLimit;
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Amps > slot1Amps + Eps)
                    {
                        slot1Amps = ordered[i].Amps;
                        slot1Idx = i;
                    }
                }
                if (slot1Idx > 0)
                {
                    var promoted = ordered[slot1Idx];
                    ordered.RemoveAt(slot1Idx);
                    ordered.Insert(0, promoted);
                }

                var module = new ModuleResult
                {
                    DimmingType = dimmingType,
                    PartNumber = partNumber,
                    ModuleCapacity = moduleCapacity
                };

                bool overloaded = false;
                double moduleTotal = 0;
                for (int i = 0; i < ordered.Count; i++)
                {
                    module.CircuitNumbers.Add(ordered[i].Circuit.CircuitNumber);
                    module.SlotAmps.Add(ordered[i].Amps);
                    moduleTotal += ordered[i].Amps;
                    if (ordered[i].Amps > limits.GetSlotLimit(i) + Eps)
                        overloaded = true;
                }
                if (moduleTotal > limits.ModuleTotalAmpLimit + Eps)
                    overloaded = true;

                module.IsOverloaded = overloaded;
                modules.Add(module);
            }
            return modules;
        }

        /// <summary>
        /// Simulates FFD bin-packing to count how many modules amps actually require.
        /// Combined via Math.Max with the count-based result so we never go below today's
        /// module count when amps fit comfortably.
        /// </summary>
        private static int SimulateFfdModuleCount(
            List<ZonesCircuitData> circuits, int moduleCapacity, ModuleAmpLimits limits)
        {
            if (circuits.Count == 0) return 0;
            double voltage = limits.Voltage <= 0 ? 120.0 : limits.Voltage;
            const double Eps = 1e-9;

            var sorted = circuits
                .Select(c => c.ApparentLoadVA / voltage)
                .OrderByDescending(a => a)
                .ToList();

            var bins = new List<(int Count, double Amps)>();
            foreach (double a in sorted)
            {
                bool placed = false;
                for (int i = 0; i < bins.Count; i++)
                {
                    if (bins[i].Count < moduleCapacity
                        && bins[i].Amps + a <= limits.ModuleTotalAmpLimit + Eps)
                    {
                        bins[i] = (bins[i].Count + 1, bins[i].Amps + a);
                        placed = true;
                        break;
                    }
                }
                if (!placed) bins.Add((1, a));
            }
            return bins.Count;
        }

        internal static IEnumerable<(string PartNumber, int Count)> GroupModulesByPartNumber(
            IEnumerable<ModuleResult> modules)
        {
            int RankOf(string t)
            {
                for (int i = 0; i < ModuleTypeOrder.Length; i++)
                    if (string.Equals(ModuleTypeOrder[i], t, StringComparison.OrdinalIgnoreCase))
                        return i;
                return ModuleTypeOrder.Length;
            }

            return modules
                .GroupBy(m => m.PartNumber ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    PartNumber = g.Key,
                    Count = g.Count(),
                    Rank = g.Min(m => RankOf(m.DimmingType))
                })
                .OrderBy(g => g.Rank)
                .ThenBy(g => g.PartNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.PartNumber, g.Count));
        }

        internal static IEnumerable<string> GetOrderedTypes(IEnumerable<string> types)
        {
            var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);

            foreach (string t in ModuleTypeOrder)
            {
                if (typeSet.TryGetValue(t, out string actual))
                    yield return actual;
            }

            foreach (string t in types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                bool isKnown = false;
                foreach (string m in ModuleTypeOrder)
                {
                    if (string.Equals(t, m, StringComparison.OrdinalIgnoreCase))
                    { isKnown = true; break; }
                }
                if (!isKnown)
                    yield return t;
            }
        }
    }
}
