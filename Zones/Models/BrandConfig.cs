#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Zones.Models
{
    public class BrandConfig
    {
        public string Name { get; }
        public int ModuleCapacity { get; }
        public int[] PanelSizes { get; }
        public Dictionary<string, string> ModulePartNumbers { get; }
        public Dictionary<int, string> PanelPartNumbers { get; }

        private BrandConfig(string name, int moduleCapacity, int[] panelSizes,
            Dictionary<string, string> modulePartNumbers,
            Dictionary<int, string> panelPartNumbers,
            Dictionary<string, string> specialDevices = null,
            HashSet<int> specialCompartmentPanelSizes = null,
            Dictionary<int, string> wireHarnessPartNumbers = null,
            string powerSupplyPartNumber = null,
            Dictionary<string, int> moduleCapacityOverrides = null,
            Dictionary<string, string> partDescriptions = null)
        {
            Name = name;
            ModuleCapacity = moduleCapacity;
            PanelSizes = panelSizes;
            ModulePartNumbers = modulePartNumbers;
            PanelPartNumbers = panelPartNumbers;
            SpecialDevices = specialDevices;
            SpecialCompartmentPanelSizes = specialCompartmentPanelSizes;
            WireHarnessPartNumbers = wireHarnessPartNumbers;
            PowerSupplyPartNumber = powerSupplyPartNumber;
            ModuleCapacityOverrides = moduleCapacityOverrides;
            PartDescriptions = partDescriptions;
        }

        public Dictionary<string, string> SpecialDevices { get; }
        public HashSet<int> SpecialCompartmentPanelSizes { get; }
        public Dictionary<int, string> WireHarnessPartNumbers { get; }
        public string PowerSupplyPartNumber { get; }
        public Dictionary<string, int> ModuleCapacityOverrides { get; }
        public Dictionary<string, string> PartDescriptions { get; }

        public int DefaultPanelSize => SpecialCompartmentPanelSizes?.Max() ?? PanelSizes.Max();

        public string GetModulePartNumber(string dimmingType)
            => ModulePartNumbers.TryGetValue(dimmingType, out var pn) ? pn : dimmingType;

        public int GetModuleCapacity(string dimmingType)
            => ModuleCapacityOverrides != null
               && ModuleCapacityOverrides.TryGetValue(dimmingType, out var cap) ? cap : ModuleCapacity;

        public string GetPartDescription(string partNumber)
            => PartDescriptions != null
               && PartDescriptions.TryGetValue(partNumber, out var desc) ? desc : partNumber;

        public int ParsePanelSizeFromCatalogNumber(string catalogNumber)
        {
            if (!string.IsNullOrEmpty(catalogNumber))
            {
                // Try to find a known panel part number that matches
                foreach (var kvp in PanelPartNumbers)
                {
                    if (string.Equals(catalogNumber, kvp.Value, StringComparison.OrdinalIgnoreCase))
                        return kvp.Key;
                }

                // Lutron: PD8-xxx → 8, PD9-xxx → 9
                if (catalogNumber.StartsWith("PD", StringComparison.OrdinalIgnoreCase)
                    && catalogNumber.Length > 2
                    && int.TryParse(catalogNumber.Substring(2, 1), out int lutronSize)
                    && PanelSizes.Contains(lutronSize))
                    return lutronSize;

                // Crestron: CAEN-7X1 → 7
                int dashIdx = catalogNumber.IndexOf('-');
                if (dashIdx >= 0 && dashIdx + 1 < catalogNumber.Length
                    && int.TryParse(catalogNumber.Substring(dashIdx + 1, 1), out int size)
                    && PanelSizes.Contains(size))
                    return size;
            }

            return PanelSizes.Min();
        }

        public static BrandConfig Lutron { get; } = new BrandConfig("Lutron", 4, new[] { 2, 4, 5, 8, 9 },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ELV", "LQSE-4A5-120-D" },
                { "0-10V", "LQSE-4T5-120-D" },
                { "Relay", "LQSE-4S8-120-D" }
            },
            new Dictionary<int, string>
            {
                { 2, "PD2-16F-120" },
                { 4, "PD4-36F-120" },
                { 5, "PD5-36F-120" },
                { 8, "PD8-59F-120" },
                { 9, "PD9-59F-120" }
            },
            new Dictionary<string, string>
            {
                { "Processor", "HQP7-2" },
                { "Digital I/O", "QSE-IO" },
                { "DMX", "QSE-CI-DMX" }
            },
            specialCompartmentPanelSizes: new HashSet<int> { 4, 8 },
            wireHarnessPartNumbers: new Dictionary<int, string>
            {
                { 2, "PDW-QS-4" },
                { 4, "PDW-QS-4" },
                { 5, "PDW-QS-5" },
                { 8, "PDW-QS-8" },
                { 9, "PDW-QS-9" }
            },
            powerSupplyPartNumber: "QSPS-DH-1-75-H",
            partDescriptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "HQP7-2", "HomeWorks QSX 2-Link Processor" },
                { "PD2-16F-120", "2 Module Feed-Through DIN Rail Power Panel" },
                { "PD4-36F-120", "4 Module DIN Rail Power Panel with LV compartment" },
                { "PD5-36F-120", "5 Module Feed-Through DIN Rail Power Panel" },
                { "PD8-59F-120", "8 Module DIN Rail Power Panel with LV compartment" },
                { "PD9-59F-120", "9 Module DIN Rail Power Panel" },
                { "LQSE-4S8-120-D", "DIN Rail Power Module (Switching)" },
                { "LQSE-4T5-120-D", "DIN Rail Power Module (0-10V and Switching)" },
                { "LQSE-4A5-120-D", "DIN Rail Power Module (LED+ Adaptive)" },
                { "QSPS-DH-1-75-H", "DIN Rail Power Supply" },
                { "PDW-QS-4", "QS Wire Harness (4-Module)" },
                { "PDW-QS-5", "QS Wire Harness (5-Module)" },
                { "PDW-QS-8", "QS Wire Harness (8-Module)" },
                { "PDW-QS-9", "QS Wire Harness (9-Module)" },
                { "QSE-IO", "QS Contact Closure Input/Output Interface" },
                { "QSE-CI-DMX", "QS DMX Output Control Interface" }
            });

        public static BrandConfig Crestron { get; } = new BrandConfig("Crestron", 8, new[] { 7 },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ELV", "CLX-2DIMU8" },
                { "0-10V", "CLX-2DIMFLV8" },
                { "Relay", "CLX-4HSW4" }
            },
            new Dictionary<int, string>
            {
                { 7, "CAEN-7X1" }
            },
            moduleCapacityOverrides: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Relay", 4 }
            },
            partDescriptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "CAEN-7X1", "7 Module Automation Enclosure" },
                { "CLX-4HSW4", "4 Channel High-Inrush Switch Module" },
                { "CLX-2DIMFLV8", "8 Channel 0-10V Dimmer Module" },
                { "CLX-2DIMU8", "8 Channel Universal Dimmer Module" }
            });
    }
}
