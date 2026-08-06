#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// The single source of the control BOM — the parts list derived from a panel allocation.
    ///
    /// Both consumers call this: the TurboZones Panel Breakdown tab (live, as the user edits panel
    /// sizes and compartment devices) and the TurboDocs Control BOM PDF. They were separate
    /// reimplementations until they were merged here, and they had already drifted — the window
    /// annotated a processor shortfall and the PDF silently rounded the quantity up without saying
    /// why. Two renderings of one purchasing document must not be able to disagree about what to
    /// order, so the shape stays: one pure builder, and the only per-consumer difference is
    /// <see cref="BomExtras.Audience"/>, which governs presentation and never quantities.
    /// </summary>
    public static class ControlBomBuilder
    {
        /// <summary>
        /// Builds the ordered BOM: Processors, Panels, Modules, Accessories, Keypads. Header rows
        /// are interleaved as <see cref="BomLineItem.IsHeader"/> entries, so the list is render-ready
        /// in document order and callers do not re-group it.
        /// </summary>
        public static List<BomLineItem> Build(
            List<PanelResult> allPanels, BrandConfig brand, BomExtras extras)
        {
            if (allPanels == null || brand == null)
                return new List<BomLineItem>();
            extras ??= new BomExtras();

            var bom = new List<BomLineItem>();

            // --- Processors ---
            // Quantity follows what the DESIGNER PLACED, not what the job is calculated to need.
            // A processor's location cannot be derived — it is an assignment the designer makes to a
            // specific panel — so the Panel Breakdown is the single source of truth for the count,
            // whether that is over or under the recommendation. The recommendation stays advisory:
            // it drives the shortfall warning on the design surface, where the fix actually happens,
            // and never silently inflates an order quantity.
            int processorCount = CountPlacedSpecialDevice(allPanels, "Processor");
            int recommendedProcessors = CalculateRecommendedProcessors(allPanels, extras);
            int bomProcessorCount = processorCount;

            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Processors", Description = "Processors" });

                string processorPn = brand.SpecialDevices != null
                    && brand.SpecialDevices.TryGetValue("Processor", out var ppn) ? ppn : "";
                string description = brand.GetPartDescription(processorPn);

                bool needsWarning = extras.Audience == BomAudience.DesignSurface
                    && processorCount < recommendedProcessors;
                if (needsWarning)
                    description += $" ({processorCount} of {recommendedProcessors} placed)";

                bom.Add(new BomLineItem
                {
                    Quantity = bomProcessorCount,
                    PartNumber = processorPn,
                    Description = description,
                    Category = "Processors",
                    IsWarning = needsWarning
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

                // Group by resolved part number so a single module type carrying multiple
                // dimming roles (e.g. LQSE-4T5 for both 0-10V and Relay) collapses to one line.
                foreach (var group in PanelAllocationService.GroupModulesByPartNumber(allModules))
                {
                    bom.Add(new BomLineItem
                    {
                        Quantity = group.Count,
                        PartNumber = group.PartNumber,
                        Description = brand.GetPartDescription(group.PartNumber),
                        Category = "Modules"
                    });
                }
            }

            // --- Accessories ---
            var accessories = new List<BomLineItem>();

            // Power supply: one per placed processor — follows the same design-is-truth rule, so a
            // job with no processor sited orders no supply rather than one for a phantom.
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

            // Special devices from panel selections (Digital I/O, DMX — excludes Processor and Empty).
            // A device whose subsystem reports its own demand is skipped here and emitted below
            // instead: the dropdown says WHERE it goes, the subsystem says HOW MANY, and counting the
            // dropdown as well would order the same interface twice.
            if (brand.SpecialDevices != null)
            {
                var specialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var panel in allPanels)
                {
                    if (!panel.HasSpecialCompartment) continue;

                    foreach (string selected in SpecialDeviceSlots(panel))
                    {
                        if (string.IsNullOrEmpty(selected)
                            || string.Equals(selected, "Empty", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(selected, "Processor", StringComparison.OrdinalIgnoreCase)
                            || IsSubsystemOwned(selected, extras))
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

            accessories.AddRange(SubsystemLines(allPanels, brand, extras));

            // Hybrid repeaters (Lutron only)
            if (extras.HybridRepeaterCount > 0
                && string.Equals(brand.Name, "Lutron", StringComparison.OrdinalIgnoreCase))
            {
                accessories.Add(new BomLineItem
                {
                    Quantity = extras.HybridRepeaterCount,
                    PartNumber = extras.HybridRepeaterPartNumber ?? "",
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
            if (extras.KeypadCount > 0 || extras.TwoGangKeypadCount > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Keypads", Description = "Keypads" });
                if (extras.KeypadCount > 0)
                {
                    bom.Add(new BomLineItem
                    {
                        Quantity = extras.KeypadCount,
                        PartNumber = "",
                        Description = "Keypad",
                        Category = "Keypads"
                    });
                }
                if (extras.TwoGangKeypadCount > 0)
                {
                    bom.Add(new BomLineItem
                    {
                        Quantity = extras.TwoGangKeypadCount,
                        PartNumber = "",
                        Description = "Two-Gang Keypad",
                        Category = "Keypads"
                    });
                }
            }

            return extras.Audience == BomAudience.IssuedDocument ? StripEmptyLines(bom) : bom;
        }

        /// <summary>
        /// Drops nothing-to-order lines and any section header left with no lines under it.
        ///
        /// A zero quantity is meaningful on the design surface — "0 processors placed" is the thing
        /// the user needs to see — but on an issued document it renders as a part number and a
        /// description with a blank quantity cell, which reads as an order for an unstated amount.
        /// Applied as a post-pass so every section gets it, including ones added later.
        /// </summary>
        private static List<BomLineItem> StripEmptyLines(List<BomLineItem> bom)
        {
            var kept = bom.Where(i => i.IsHeader || i.Quantity > 0).ToList();

            var result = new List<BomLineItem>();
            for (int i = 0; i < kept.Count; i++)
            {
                // A header survives only if a real line follows it before the next header.
                if (kept[i].IsHeader && (i + 1 >= kept.Count || kept[i + 1].IsHeader))
                    continue;
                result.Add(kept[i]);
            }
            return result;
        }

        /// <summary>
        /// The BOM lines a control subsystem contributes — the parts it solved for, plus a warning line
        /// when it could not solve at all.
        ///
        /// Quantity comes from the subsystem, NOT from what the designer placed, and that is a real
        /// departure from the processor rule one section up. The difference is what is derivable: a
        /// processor's count is inseparable from its location, which only a human can decide, whereas
        /// TurboDMX computes the interface count from channel math and the compartment dropdown cannot
        /// even express it — a panel holds one special device (two on an LV21), so a job needing four
        /// interfaces has no way to say so by placing. Here the dropdown states location and the
        /// subsystem states quantity. A shortfall still surfaces, on the design surface, the same way a
        /// processor shortfall does.
        /// </summary>
        private static IEnumerable<BomLineItem> SubsystemLines(
            List<PanelResult> allPanels, BrandConfig brand, BomExtras extras)
        {
            var lines = new List<BomLineItem>();
            if (extras.SubsystemDemands == null) return lines;

            foreach (var demand in extras.SubsystemDemands)
            {
                if (demand == null) continue;

                // Could not solve. Worth a line rather than silence: there is real hardware that will
                // not make it onto the order, and the reason is the subsystem's own words. Design
                // surface only — a purchasing document is not where a half-declared design gets fixed.
                if (demand.HasDiagnostic && extras.Audience == BomAudience.DesignSurface)
                {
                    lines.Add(new BomLineItem
                    {
                        Quantity = 0,
                        PartNumber = "",
                        Description = $"{demand.Subsystem}: {demand.Diagnostic}",
                        Category = "Accessories",
                        IsWarning = true
                    });
                }

                foreach (var part in demand.Parts)
                {
                    if (part == null || part.Quantity <= 0) continue;

                    string description = part.Description ?? brand.GetPartDescription(part.PartNumber);

                    int placed = CountPlacedSpecialDevice(allPanels, demand.Subsystem);
                    bool needsWarning = extras.Audience == BomAudience.DesignSurface
                        && placed < part.Quantity;
                    if (needsWarning)
                        description += $" ({placed} of {part.Quantity} placed)";

                    lines.Add(new BomLineItem
                    {
                        Quantity = part.Quantity,
                        PartNumber = part.PartNumber,
                        Description = description,
                        Category = "Accessories",
                        IsWarning = needsWarning
                    });
                }
            }
            return lines;
        }

        /// <summary>True when a compartment selection's parts are counted by a subsystem instead of by
        /// the dropdown. Matched on the special-device NAME ("DMX"), which is what both the dropdown
        /// and <see cref="ControlSubsystemDemand.Subsystem"/> use.</summary>
        private static bool IsSubsystemOwned(string specialDevice, BomExtras extras)
        {
            if (extras.SubsystemDemands == null) return false;
            foreach (var demand in extras.SubsystemDemands)
            {
                if (demand != null && string.Equals(demand.Subsystem, specialDevice,
                                                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Processors implied by the job's QS-link demand: devices and loads each cap a link, links
        /// cap a processor. Hybrid repeaters ride their own CCA link rather than the QS link, so they
        /// add capacity demand instead of consuming it.
        /// </summary>
        public static int CalculateRecommendedProcessors(List<PanelResult> allPanels, BomExtras extras)
        {
            if (allPanels == null) return 1;
            extras ??= new BomExtras();

            // Compartment devices — each counts as 1 device on a QS link. A subsystem-owned device is
            // excluded here and picked up from its demand below, which knows the real count; taking
            // both would charge the link twice for the same interfaces.
            int specialDeviceCount = 0;
            foreach (string device in new[] { "Digital I/O", "DMX" })
            {
                if (!IsSubsystemOwned(device, extras))
                    specialDeviceCount += CountPlacedSpecialDevice(allPanels, device);
            }

            // Subsystem demand on the link. Devices and loads are independent budgets: a QSE-CI-DMX is
            // "1 QS device and 0 zones", while each of its DMX channels is a switch leg. So a sparse
            // DMX job pressures the device cap and a dense one pressures the leg cap, and neither
            // number can be derived from the other.
            int subsystemDevices = 0;
            int subsystemLoads = 0;
            if (extras.SubsystemDemands != null)
            {
                foreach (var demand in extras.SubsystemDemands)
                {
                    if (demand == null) continue;
                    subsystemDevices += demand.LinkDevices;
                    subsystemLoads += demand.LinkLoads;
                }
            }

            int totalDevices = allPanels.Sum(p => p.DeviceCount)
                + extras.KeypadCount + extras.TwoGangKeypadCount * 2
                + specialDeviceCount + subsystemDevices;
            int totalLoads = allPanels.Sum(p => p.LoadCount) + subsystemLoads;

            int qsLinksNeeded = Math.Max(
                (int)Math.Ceiling((double)totalDevices / ProcessorLink.MaxDevices),
                (int)Math.Ceiling((double)totalLoads / ProcessorLink.MaxLoads));
            qsLinksNeeded = Math.Max(qsLinksNeeded, 1);

            int ccaLinksNeeded = extras.HybridRepeaterCount > 0
                ? Math.Max(1, (int)Math.Ceiling((double)extras.HybridRepeaterCount / ProcessorLink.MaxDevices))
                : 0;

            int totalLinksNeeded = qsLinksNeeded + ccaLinksNeeded;
            return Math.Max(1, (int)Math.Ceiling((double)totalLinksNeeded / 2));
        }

        /// <summary>Counts how many compartment slots across all panels hold the named device.</summary>
        private static int CountPlacedSpecialDevice(List<PanelResult> allPanels, string deviceName)
        {
            int count = 0;
            foreach (var panel in allPanels)
            {
                if (!panel.HasSpecialCompartment) continue;
                foreach (string selected in SpecialDeviceSlots(panel))
                {
                    if (string.Equals(selected, deviceName, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
            }
            return count;
        }

        /// <summary>A panel's occupied compartment slots — one, or two on a dual-compartment panel (LV21).</summary>
        private static IEnumerable<string> SpecialDeviceSlots(PanelResult panel)
        {
            yield return panel.SelectedSpecialDevice;
            if (panel.HasDualSpecialCompartment)
                yield return panel.SelectedSpecialDevice2;
        }
    }

    /// <summary>
    /// The BOM inputs that do not come from the panel allocation — project-wide device counts plus
    /// who the list is for.
    /// </summary>
    public class BomExtras
    {
        public int KeypadCount { get; set; }
        public int TwoGangKeypadCount { get; set; }
        public int HybridRepeaterCount { get; set; }
        public string HybridRepeaterPartNumber { get; set; }

        /// <summary>What the control subsystems report they need — DMX today, DALI later. Null or empty
        /// means nothing to add, which is the shape a job with no subsystem hardware produces and the
        /// shape a caller that has no provider wired up produces; both are correct.</summary>
        public IReadOnlyList<ControlSubsystemDemand> SubsystemDemands { get; set; }

        /// <summary>Who the BOM is being built for. Defaults to <see cref="BomAudience.IssuedDocument"/>
        /// — the conservative choice, since design-state commentary leaking onto a purchasing
        /// document is the worse failure.</summary>
        public BomAudience Audience { get; set; } = BomAudience.IssuedDocument;
    }

    /// <summary>
    /// Who a control BOM is being rendered for. The parts and quantities are identical either way —
    /// this governs only what the list says ABOUT itself, which is the sole legitimate difference
    /// between the two consumers. Anything else that diverges is a bug.
    /// </summary>
    public enum BomAudience
    {
        /// <summary>The issued Control BOM PDF. Nothing to order is nothing to print: zero-quantity
        /// lines and their orphaned headers are dropped, and no shortfall commentary appears.</summary>
        IssuedDocument,

        /// <summary>The live TurboZones Panel Breakdown. Shows zero-quantity lines and annotates a
        /// placed-below-recommended shortfall with "(N of M placed)" plus
        /// <see cref="BomLineItem.IsWarning"/> — this is where the user sees a gap and fixes it.</summary>
        DesignSurface
    }
}
