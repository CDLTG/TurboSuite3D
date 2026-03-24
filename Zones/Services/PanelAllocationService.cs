#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
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
            Dictionary<string, string> specialDeviceSelections = null,
            Dictionary<string, int> panelSizeOverrides = null)
        {
            var unassigned = new List<ZonesCircuitData>();

            // Group circuits by zone number, filtering out DUMMY and unassigned
            var circuitsByZone = new Dictionary<int, List<ZonesCircuitData>>();
            foreach (var circuit in circuits)
            {
                if (string.IsNullOrWhiteSpace(circuit.PanelName))
                {
                    unassigned.Add(circuit);
                    continue;
                }

                // Skip DUMMY panel
                if (string.Equals(circuit.PanelName, "DUMMY", StringComparison.OrdinalIgnoreCase))
                    continue;

                int zone = ParseLocationNumber(circuit.PanelName);
                if (!circuitsByZone.ContainsKey(zone))
                    circuitsByZone[zone] = new List<ZonesCircuitData>();
                circuitsByZone[zone].Add(circuit);
            }

            var result = new PanelAllocationResult
            {
                TotalCircuits = circuits.Count
            };

            int totalModules = 0;

            foreach (var zone in circuitsByZone.Keys.OrderBy(z => z))
            {
                var zoneCircuits = circuitsByZone[zone];

                // Group all circuits in this zone by dimming type
                var circuitsByType = zoneCircuits
                    .GroupBy(c => c.DimmingType)
                    .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CircuitNumber).ToList());

                // Calculate modules needed per dimming type
                var moduleCountByType = new Dictionary<string, int>();
                foreach (var kvp in circuitsByType)
                {
                    int modules = CalculateModuleCount(kvp.Value.Count, brand.GetModuleCapacity(kvp.Key));
                    moduleCountByType[kvp.Key] = modules;
                }

                int zoneTotalModules = moduleCountByType.Values.Sum();
                totalModules += zoneTotalModules;

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
                    LocationNumber = zone,
                    TotalCircuits = zoneCircuits.Count
                };

                var panelResults = new List<PanelResult>();
                foreach (var (name, size) in panelSizes)
                {
                    var panelResult = new PanelResult
                    {
                        PanelName = name,
                        SelectedPanelSize = size,
                        SpecialCompartmentPanelSizes = brand.SpecialCompartmentPanelSizes,
                        AvailablePanelSizes = brand.PanelSizes.OrderBy(s => s)
                            .Select(s => new PanelSizeOption
                            {
                                Size = s,
                                DisplayName = brand.PanelPartNumbers.TryGetValue(s, out var pn)
                                    ? pn.Split('-')[0] : s.ToString()
                            }).ToList()
                    };

                    // Set up special device options if applicable
                    if (panelResult.HasSpecialCompartment && brand.SpecialDevices != null)
                    {
                        panelResult.SpecialDeviceOptions = new List<string> { "Empty" };
                        panelResult.SpecialDeviceOptions.AddRange(brand.SpecialDevices.Keys);
                        panelResult.SpecialDevicePartNumbers = brand.SpecialDevices;
                        panelResult.SelectedSpecialDevice = "Empty";
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

            result.TotalModules = totalModules;
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

            // Build modules for each panel using its allocated counts
            foreach (var panel in panels)
            {
                var panelAlloc = allocation[panel];
                int circuitOffset = 0;

                foreach (var type in orderedTypes)
                {
                    if (!panelAlloc.TryGetValue(type, out int moduleCount) || moduleCount == 0)
                        continue;

                    if (!circuitsByType.TryGetValue(type, out var typeCircuits))
                        continue;

                    int moduleCapacity = brand.GetModuleCapacity(type);

                    // Calculate how many circuits go on this panel's modules for this type
                    int totalModulesForType = moduleCountByType[type];
                    int totalCircuitsForType = typeCircuits.Count;

                    // Proportional circuit share for this panel
                    int circuitsForPanel = (int)Math.Ceiling((double)totalCircuitsForType * moduleCount / totalModulesForType);
                    circuitsForPanel = Math.Min(circuitsForPanel, moduleCount * moduleCapacity);
                    circuitsForPanel = Math.Min(circuitsForPanel, totalCircuitsForType - circuitOffset);

                    var panelCircuits = typeCircuits.Skip(circuitOffset).Take(circuitsForPanel).ToList();
                    circuitOffset += panelCircuits.Count;

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
