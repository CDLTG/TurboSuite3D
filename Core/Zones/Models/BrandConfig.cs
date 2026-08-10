#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Zones.Models
{
    public class ModuleAmpLimits
    {
        public double Slot1AmpLimit { get; }
        public double DefaultSlotAmpLimit { get; }
        public double ModuleTotalAmpLimit { get; }
        public double Voltage { get; }

        public ModuleAmpLimits(double slot1, double defaultSlot, double moduleTotal, double voltage = 120.0)
        {
            Slot1AmpLimit = slot1;
            DefaultSlotAmpLimit = defaultSlot;
            ModuleTotalAmpLimit = moduleTotal;
            Voltage = voltage;
        }

        public double GetSlotLimit(int slotIndex) => slotIndex == 0 ? Slot1AmpLimit : DefaultSlotAmpLimit;
    }

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
            HashSet<int> dualCompartmentPanelSizes = null,
            Dictionary<int, string> panelDisplayNames = null,
            Dictionary<int, string> wireHarnessPartNumbers = null,
            string powerSupplyPartNumber = null,
            Dictionary<string, int> moduleCapacityOverrides = null,
            Dictionary<string, string> partDescriptions = null,
            Dictionary<string, ModuleAmpLimits> ampLimits = null,
            Dictionary<string, int> devicePduDraw = null,
            int powerSupplyPdu = 0,
            Dictionary<int, int> powerSupplyCapacityByPanelSize = null)
        {
            Name = name;
            ModuleCapacity = moduleCapacity;
            PanelSizes = panelSizes;
            ModulePartNumbers = modulePartNumbers;
            PanelPartNumbers = panelPartNumbers;
            SpecialDevices = specialDevices;
            SpecialCompartmentPanelSizes = specialCompartmentPanelSizes;
            DualCompartmentPanelSizes = dualCompartmentPanelSizes;
            PanelDisplayNames = panelDisplayNames;
            WireHarnessPartNumbers = wireHarnessPartNumbers;
            PowerSupplyPartNumber = powerSupplyPartNumber;
            ModuleCapacityOverrides = moduleCapacityOverrides;
            PartDescriptions = partDescriptions;
            AmpLimits = ampLimits;
            DevicePduDraw = devicePduDraw;
            PowerSupplyPdu = powerSupplyPdu;
            PowerSupplyCapacityByPanelSize = powerSupplyCapacityByPanelSize;
        }

        public Dictionary<string, string> SpecialDevices { get; }
        public HashSet<int> SpecialCompartmentPanelSizes { get; }
        public HashSet<int> DualCompartmentPanelSizes { get; }
        public Dictionary<int, string> PanelDisplayNames { get; }
        public Dictionary<int, string> WireHarnessPartNumbers { get; }
        public string PowerSupplyPartNumber { get; }
        public Dictionary<string, int> ModuleCapacityOverrides { get; }
        public Dictionary<string, string> PartDescriptions { get; }
        public Dictionary<string, ModuleAmpLimits> AmpLimits { get; }

        /// <summary>
        /// Signed QS-link power draw of each device that has one, keyed by the name the compartment
        /// dropdown and <see cref="ControlSubsystemDemand.Subsystem"/> use ("Processor", "Digital I/O",
        /// "DMX") plus "Keypad" for the poured keypads. A supply <i>contributes</i> PDU
        /// (<see cref="PowerSupplyPdu"/>, positive); everything here <i>draws</i> it (negative).
        ///
        /// <b>What is deliberately absent, and why it must stay absent.</b> Lutron's PDU budget is a
        /// <i>terminal-2</i> (V+) fact: a device draws PDU only if it takes bus power off the link's V+
        /// rail. The dimming modules (LQSE-*) are line-powered and take nothing from V+ — their whole
        /// job is downstream of the panel's own supply — so they draw <b>0 PDU</b> and are not in this
        /// table. That is not an omission to be "fixed" by adding the LQSE at some guessed value: a
        /// module's presence on a link is a <i>terminal-3/4</i> (MUX/data) fact, which is what the
        /// device/leg budgets in <see cref="ControlLinkPacker"/> count, and is independent of PDU.
        /// Panels are likewise 0 (they are enclosures, not bus loads). If a future device belongs here,
        /// it belongs because it draws V+ bus power, not because it sits on the link.
        /// </summary>
        public Dictionary<string, int> DevicePduDraw { get; }

        /// <summary>PDU a single QS-link power supply contributes (QSPS-DH-1-75-H → 75). Zero when the
        /// brand has no PDU model, which is the signal to skip PDU sizing entirely.</summary>
        public int PowerSupplyPdu { get; }

        /// <summary>How many QSPS supply positions a panel of each module capacity provides — the
        /// denominator of the <b>global</b> feasibility check (total supplies needed vs the sum of this
        /// across every placed panel). LV21→2, PD4→1, PD8→1, the rest→0. Not a per-link check: there is
        /// no physical panel→link assignment in the model, so a shortfall means "the panel mix cannot
        /// hold the count", answered by a different panel rather than another placement.</summary>
        public Dictionary<int, int> PowerSupplyCapacityByPanelSize { get; }

        /// <summary>Signed PDU draw of a named device, or 0 when it has none (modules, panels, and any
        /// brand with no PDU model all land here).</summary>
        public int GetDevicePduDraw(string deviceName)
            => DevicePduDraw != null
               && !string.IsNullOrEmpty(deviceName)
               && DevicePduDraw.TryGetValue(deviceName, out var pdu) ? pdu : 0;

        /// <summary>Supply positions a panel of the given module capacity provides, 0 when unlisted.</summary>
        public int GetPowerSupplySlots(int panelSize)
            => PowerSupplyCapacityByPanelSize != null
               && PowerSupplyCapacityByPanelSize.TryGetValue(panelSize, out var slots) ? slots : 0;

        /// <summary>
        /// Amp limits are keyed by module part number so that a dimming type sharing
        /// another module (e.g. Lutron Relay loads riding on LQSE-4T5) inherits the
        /// limits of the actual physical module.
        /// </summary>
        public ModuleAmpLimits GetAmpLimits(string partNumber)
            => AmpLimits != null
               && !string.IsNullOrEmpty(partNumber)
               && AmpLimits.TryGetValue(partNumber, out var limits) ? limits : null;

        public ModuleAmpLimits GetAmpLimitsForDimmingType(string dimmingType)
            => GetAmpLimits(GetModulePartNumber(dimmingType));

        public int DefaultPanelSize => SpecialCompartmentPanelSizes?.Max() ?? PanelSizes.Max();

        public string GetModulePartNumber(string dimmingType)
            => ModulePartNumbers.TryGetValue(dimmingType, out var pn) ? pn : dimmingType;

        public int GetModuleCapacity(string dimmingType)
            => ModuleCapacityOverrides != null
               && ModuleCapacityOverrides.TryGetValue(dimmingType, out var cap) ? cap : ModuleCapacity;

        public string GetPartDescription(string partNumber)
            => PartDescriptions != null
               && PartDescriptions.TryGetValue(partNumber, out var desc) ? desc : partNumber;

        public string GetPanelDisplayName(int size)
        {
            if (PanelDisplayNames != null && PanelDisplayNames.TryGetValue(size, out var name))
                return name;
            if (PanelPartNumbers.TryGetValue(size, out var pn))
                return pn.Split('-')[0];
            return size.ToString();
        }

        public static BrandConfig Lutron { get; } = CreateLutron(useDedicatedRelayModule: false);

        public static BrandConfig CreateLutron(bool useDedicatedRelayModule)
            => new BrandConfig("Lutron", 4, new[] { 0, 2, 4, 5, 8, 9 },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ELV", "LQSE-4A5-120-D" },
                { "0-10V", "LQSE-4T5-120-D" },
                { "Relay", useDedicatedRelayModule ? "LQSE-4S8-120-D" : "LQSE-4T5-120-D" }
            },
            new Dictionary<int, string>
            {
                { 0, "HQ-LV21-120" },
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
            specialCompartmentPanelSizes: new HashSet<int> { 0, 4, 8 },
            dualCompartmentPanelSizes: new HashSet<int> { 0 },
            panelDisplayNames: new Dictionary<int, string> { { 0, "LV21" } },
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
                { "HQ-LV21-120", "LV Compartment Panel (2-slot)" },
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
            },
            ampLimits: new Dictionary<string, ModuleAmpLimits>(StringComparer.OrdinalIgnoreCase)
            {
                { "LQSE-4A5-120-D", new ModuleAmpLimits(slot1: 6.6, defaultSlot: 4.2, moduleTotal: 16.0) },
                { "LQSE-4T5-120-D", new ModuleAmpLimits(slot1: 5.0, defaultSlot: 5.0, moduleTotal: 20.0) },
                { "LQSE-4S8-120-D", new ModuleAmpLimits(slot1: 8.0, defaultSlot: 8.0, moduleTotal: 16.0) }
            },
            // Signed QS-link PDU draws. Processor −8 is billed by the BOM-side supply sizer, not the
            // packer (it is a per-processor fact, and the packer is identity-blind); it lives here so
            // the magnitude is data, not a literal in the sizer. Keypad −1 (two-gang is −2 by counting
            // as two keypads). QSE-IO −3, QSE-CI-DMX −2. Modules and panels are absent by design — see
            // DevicePduDraw's summary. Quoted from Lutron 369405ab (QS Link Power Draw Units).
            devicePduDraw: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Processor", -8 },
                { "Digital I/O", -3 },
                { "DMX", -2 },
                { "Keypad", -1 }
            },
            powerSupplyPdu: 75,
            // Supply positions per panel: LV21 holds two, PD4 and PD8 one each, the feed-through panels
            // none. The global feasibility denominator — not a per-link capacity.
            powerSupplyCapacityByPanelSize: new Dictionary<int, int>
            {
                { 0, 2 }, { 2, 0 }, { 4, 1 }, { 5, 0 }, { 8, 1 }, { 9, 0 }
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
