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
        private static readonly string[] ModuleTypeOrder = { "Relay", "0-10V", "ELV" };

        /// <summary>
        /// Groups circuits by their Revit panel assignment, creates modules per panel,
        /// and determines panel sizes from Catalog Number1. Derives locations from
        /// panel name format {number}-{letter}.
        /// Circuits without a panel assignment are returned as unassigned.
        /// </summary>
        public static (PanelAllocationResult Result, List<ZonesCircuitData> Unassigned) BuildPanelBreakdown(
            List<ZonesCircuitData> circuits,
            BrandConfig brand,
            Dictionary<string, string> panelCatalogNumbers = null,
            Dictionary<string, string> specialDeviceSelections = null,
            HashSet<string> knownPanelNames = null)
        {
            var unassigned = new List<ZonesCircuitData>();

            // Group circuits by panel name
            var circuitsByPanel = new Dictionary<string, List<ZonesCircuitData>>(StringComparer.OrdinalIgnoreCase);
            foreach (var circuit in circuits)
            {
                if (string.IsNullOrWhiteSpace(circuit.PanelName))
                {
                    unassigned.Add(circuit);
                    continue;
                }

                if (!circuitsByPanel.ContainsKey(circuit.PanelName))
                    circuitsByPanel[circuit.PanelName] = new List<ZonesCircuitData>();
                circuitsByPanel[circuit.PanelName].Add(circuit);
            }

            // Ensure known panels always appear (even if they have 0 circuits)
            if (knownPanelNames != null)
            {
                foreach (var panelName in knownPanelNames)
                {
                    if (!circuitsByPanel.ContainsKey(panelName))
                        circuitsByPanel[panelName] = new List<ZonesCircuitData>();
                }
            }

            // Parse panel names into location groups: {number}-{letter} format
            // Panels that don't match get location 0
            var panelLocations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var panelName in circuitsByPanel.Keys)
            {
                panelLocations[panelName] = ParseLocationNumber(panelName);
            }

            // Group panels by location, then sort by panel name within each location
            var locationGroups = circuitsByPanel.Keys
                .GroupBy(name => panelLocations[name])
                .OrderBy(g => g.Key);

            var result = new PanelAllocationResult
            {
                TotalCircuits = circuits.Count
            };

            int totalModules = 0;

            foreach (var locGroup in locationGroups)
            {
                var locationResult = new LocationResult
                {
                    LocationNumber = locGroup.Key
                };

                foreach (var panelName in locGroup.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    var panelCircuits = circuitsByPanel[panelName];

                    // Group this panel's circuits by dimming type
                    var circuitsByType = panelCircuits
                        .GroupBy(c => c.DimmingType)
                        .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CircuitNumber).ToList());

                    // Calculate modules per type
                    var moduleCountByType = new Dictionary<string, int>();
                    foreach (var kvp in circuitsByType)
                    {
                        int modules = CalculateModuleCount(kvp.Value.Count, brand.GetModuleCapacity(kvp.Key));
                        moduleCountByType[kvp.Key] = modules;
                    }

                    int panelTotalModules = moduleCountByType.Values.Sum();
                    totalModules += panelTotalModules;

                    // Determine panel size from Catalog Number1
                    int panelSize = brand.PanelSizes.Min();
                    if (panelCatalogNumbers != null
                        && panelCatalogNumbers.TryGetValue(panelName, out string catalogNum))
                    {
                        panelSize = brand.ParsePanelSizeFromCatalogNumber(catalogNum);
                    }

                    var panelResult = new PanelResult
                    {
                        PanelName = panelName,
                        SelectedPanelSize = panelSize,
                        SpecialCompartmentPanelSize = brand.SpecialCompartmentPanelSize
                    };

                    // Set up special device options if applicable
                    if (panelResult.HasSpecialCompartment && brand.SpecialDevices != null)
                    {
                        panelResult.SpecialDeviceOptions = new List<string> { "Empty" };
                        panelResult.SpecialDeviceOptions.AddRange(brand.SpecialDevices.Keys);
                        panelResult.SpecialDevicePartNumbers = brand.SpecialDevices;
                        panelResult.SelectedSpecialDevice = "Empty";
                    }

                    // Build modules and assign to panel
                    var orderedTypes = GetOrderedTypes(circuitsByType.Keys).ToList();
                    foreach (var type in orderedTypes)
                    {
                        if (!circuitsByType.ContainsKey(type) || !moduleCountByType.ContainsKey(type))
                            continue;

                        var modules = BuildModules(
                            type,
                            circuitsByType[type],
                            moduleCountByType[type],
                            brand.GetModuleCapacity(type),
                            brand);

                        panelResult.Modules.AddRange(modules);
                    }

                    locationResult.Panels.Add(panelResult);
                    locationResult.TotalModules += panelTotalModules;
                    locationResult.TotalCircuits += panelCircuits.Count;
                }

                result.Locations.Add(locationResult);
            }

            result.TotalModules = totalModules;
            return (result, unassigned);
        }

        /// <summary>
        /// Parses location number from panel name in {number}-{letter} format.
        /// Returns 0 if the panel name doesn't match the expected format.
        /// </summary>
        internal static int ParseLocationNumber(string panelName)
        {
            if (string.IsNullOrEmpty(panelName))
                return 0;

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
