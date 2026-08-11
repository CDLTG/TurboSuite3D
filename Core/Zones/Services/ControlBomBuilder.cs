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
        /// Builds the ordered BOM: Processors, Panels, Modules, Accessories, Shades, Keypads. Header rows
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
            int recommendedProcessors = CalculateRecommendedProcessors(allPanels, extras, brand);
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

            // Power supply: sized from the QS-link PDU budget, not one-per-processor. A job with no
            // processor sited still orders none (the sizer returns 0), so no supply for a phantom.
            if (!string.IsNullOrEmpty(brand.PowerSupplyPartNumber))
            {
                var supply = SizePowerSupplies(allPanels, brand, extras);

                // A count of 0 means no processor is sited yet: nothing to order, and the processor line
                // already carries that signal ("0 of N placed"). Unlike the processor, the supply has no
                // placement of its own to annotate, so a bare "0" is noise — drop it on both surfaces
                // (the issued document strips it anyway). A sited processor always needs ≥1, so this
                // never hides a real order.
                if (supply.Quantity > 0)
                {
                    string description = brand.GetPartDescription(brand.PowerSupplyPartNumber);

                    // Global infeasibility: the panels cannot physically hold the supplies the demand
                    // needs. A different warning shape from "(N of M placed)" — the fix is a bigger/other
                    // panel, not another placement — and design-surface only, like every other annotation.
                    bool infeasible = extras.Audience == BomAudience.DesignSurface
                        && supply.Quantity > supply.SlotsAvailable;
                    if (infeasible)
                        description += $" ({supply.Quantity} needed, panels hold {supply.SlotsAvailable})";

                    accessories.Add(new BomLineItem
                    {
                        Quantity = supply.Quantity,
                        PartNumber = brand.PowerSupplyPartNumber,
                        Description = description,
                        Category = "Accessories",
                        IsWarning = infeasible
                    });
                }
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
            // Quantity is what the designer placed, exactly as it is for processors: a compartment
            // device has to go SOMEWHERE, and only a human decides where. A subsystem that solved a
            // requirement annotates this line rather than replacing it.
            if (brand.SpecialDevices != null)
            {
                var specialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var panel in allPanels)
                {
                    foreach (string selected in panel.CompartmentSlots)
                    {
                        if (!ControlLinkPacker.IsDeviceSelection(selected))
                            continue;

                        if (!specialCounts.ContainsKey(selected))
                            specialCounts[selected] = 0;
                        specialCounts[selected]++;
                    }
                }

                // A subsystem with a requirement gets a line even when nothing is placed: that zero IS
                // the signal ("0 of 4 placed"), the same one an unplaced processor shows. Stripped from
                // the issued document like any other zero.
                if (extras.SubsystemDemands != null)
                {
                    foreach (var demand in extras.SubsystemDemands)
                    {
                        if (demand == null) continue;
                        if (brand.SpecialDevices.ContainsKey(demand.Subsystem)
                            && RequiredFor(demand.Subsystem, extras) > 0
                            && !specialCounts.ContainsKey(demand.Subsystem))
                            specialCounts[demand.Subsystem] = 0;
                    }
                }

                foreach (var kvp in specialCounts)
                {
                    string partNumber = brand.SpecialDevices.TryGetValue(kvp.Key, out var spn) ? spn : "";
                    string description = brand.GetPartDescription(partNumber);

                    int required = RequiredFor(kvp.Key, extras);
                    bool needsWarning = extras.Audience == BomAudience.DesignSurface
                        && kvp.Value < required;
                    if (needsWarning)
                        description += $" ({kvp.Value} of {required} placed)";

                    accessories.Add(new BomLineItem
                    {
                        Quantity = kvp.Value,
                        PartNumber = partNumber,
                        Description = description,
                        Category = "Accessories",
                        IsWarning = needsWarning
                    });
                }
            }

            // Subsystem parts. Shades carry their own "Shades" category and get their own section below;
            // every other subsystem's external parts join Accessories.
            var subsystemLines = SubsystemLines(brand, extras).ToList();
            var shadeLines = subsystemLines.Where(l => l.Category == "Shades").ToList();
            accessories.AddRange(subsystemLines.Where(l => l.Category != "Shades"));

            // Hybrid repeaters (Lutron only), one line per catalog number.
            if (string.Equals(brand.Name, "Lutron", StringComparison.OrdinalIgnoreCase))
            {
                accessories.AddRange(TallyLines(extras.HybridRepeaterTallies, "Accessories",
                    extras.Audience, "Hybrid Repeater",
                    "HWQS Hybrid Wired/Wireless RF System Repeater"));
            }

            if (accessories.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Accessories", Description = "Accessories" });
                bom.AddRange(accessories);
            }

            // --- Shades ---
            // The Sivoia QS shade panels (QSPS-10PNL), kept apart from the general Accessories.
            if (shadeLines.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Shades", Description = "Shades" });
                bom.AddRange(shadeLines);
            }

            // --- Keypads ---
            // One line per catalog number. There is deliberately no gang split here: a two-gang
            // keypad is a different model with its own catalog number, so the lines separate on their
            // own, and "Two Gang" goes back to being what it is — a device-count multiplier for the
            // link math, which is the only place it was ever needed. This section used to print the
            // words "Keypad" and "Two-Gang Keypad" against blank part numbers.
            var keypadLines = TallyLines(extras.KeypadTallies, "Keypads", extras.Audience, "Keypad");
            if (keypadLines.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Keypads", Description = "Keypads" });
                bom.AddRange(keypadLines);
            }

            return extras.Audience == BomAudience.IssuedDocument ? StripEmptyLines(bom) : bom;
        }

        /// <summary>
        /// Turns counted parts into order lines, one per catalog number.
        ///
        /// A type carrying no catalog number, or a quantity rule that would not parse, still gets its
        /// line — the devices are placed and the quantity is real, so dropping it would understate the
        /// order, and <see cref="BomAudience"/> is not allowed to change a quantity.
        ///
        /// With no catalog number the part column falls back to <paramref name="fallbackPartNumber"/> —
        /// the generic word for the thing, "Keypad". A row reading <c>12 · Keypad</c> is at least
        /// legible as a line item; the blank it replaced read as an order for an unnamed part. The
        /// design surface additionally flags the row, which is the signal that a real number is
        /// missing; the issued document just prints it, because a purchasing document is not where a
        /// family gets fixed.
        /// </summary>
        private static List<BomLineItem> TallyLines(
            IReadOnlyList<ControlDeviceTally> tallies, string category, BomAudience audience,
            string fallbackPartNumber, string description = "")
        {
            var lines = new List<BomLineItem>();
            if (tallies == null) return lines;

            foreach (var tally in tallies)
            {
                if (tally == null || tally.Quantity <= 0) continue;

                bool missing = !tally.HasCatalogNumber;
                bool flag = (missing || tally.HasDiagnostic)
                    && audience == BomAudience.DesignSurface;

                lines.Add(new BomLineItem
                {
                    Quantity = tally.Quantity,
                    PartNumber = missing ? fallbackPartNumber : tally.CatalogNumber,

                    // A bad quantity rule is the one case worth spelling out, since the number on the
                    // line is a fallback rather than the authored intent, and it displaces the
                    // description for as long as it is unfixed. Otherwise the type's own words win
                    // over the generic per-category text, being the more specific of the two.
                    Description = flag && tally.HasDiagnostic
                        ? tally.Diagnostic
                        : (string.IsNullOrEmpty(tally.Description) ? description : tally.Description),
                    Category = category,
                    IsWarning = flag
                });
            }
            return lines;
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
        /// What a subsystem contributes beyond the compartment lines: its reason for not solving, and
        /// any part that has no compartment to be placed into.
        ///
        /// The rule is <b>placement wins wherever placement is possible</b>. A QSE-CI-DMX is a
        /// compartment device, so its quantity follows the dropdown exactly as a processor's does — the
        /// subsystem's solve becomes a requirement that annotates the line, never an order that
        /// overrides it. TurboDMX says "this job needs four"; the designer says "and here is where the
        /// four go"; the purchase order follows the designer. A part with no compartment (the DALI DIN
        /// module, when it lands) has no placement to defer to and is emitted at its solved quantity.
        /// </summary>
        private static IEnumerable<BomLineItem> SubsystemLines(BrandConfig brand, BomExtras extras)
        {
            var lines = new List<BomLineItem>();
            if (extras.SubsystemDemands == null) return lines;

            foreach (var demand in extras.SubsystemDemands)
            {
                if (demand == null) continue;

                // Shades get their own section, apart from the general Accessories; every other
                // subsystem's external parts land in Accessories.
                string category = string.Equals(demand.Subsystem, ShadeSolver.SubsystemName,
                    StringComparison.OrdinalIgnoreCase) ? "Shades" : "Accessories";

                // Something is wrong and a human has to fix it: a design that would not solve, or one
                // that solved over incomplete input. Worth a line rather than silence, in the
                // subsystem's own words. Design surface only — a purchasing document is not where a
                // half-declared design gets fixed, and the reason is not orderable.
                if (demand.HasDiagnostic && extras.Audience == BomAudience.DesignSurface)
                {
                    lines.Add(new BomLineItem
                    {
                        Quantity = 0,
                        PartNumber = "",
                        Description = $"{demand.Subsystem}: {demand.Diagnostic}",
                        Category = category,
                        IsWarning = true
                    });
                }

                foreach (var part in demand.Parts)
                {
                    if (part == null || part.Quantity <= 0) continue;

                    // A compartment part is ordered by placement, on the line built above — never from
                    // here. Keyed on the MOUNT, not on whether this brand happens to define a
                    // compartment for it: Crestron declares no special devices at all, and reading that
                    // as "no compartment anywhere" dropped a Lutron QSE-CI-DMX onto a Crestron BOM at
                    // full solve quantity. A part that cannot be placed on this brand is ordered by
                    // nobody, which is the correct answer.
                    if (part.Mount == DemandMount.LvCompartment) continue;

                    lines.Add(new BomLineItem
                    {
                        Quantity = part.Quantity,
                        PartNumber = part.PartNumber,
                        Description = part.Description ?? brand.GetPartDescription(part.PartNumber),
                        Category = category
                    });
                }
            }
            return lines;
        }

        /// <summary>How many of a compartment device the subsystems say the job needs, or 0 when
        /// nothing speaks for it. Matched on the special-device NAME ("DMX"), which is what both the
        /// dropdown and <see cref="ControlSubsystemDemand.Subsystem"/> use. Only compartment-mounted
        /// parts count — a subsystem's DIN or external parts are ordered elsewhere and would inflate
        /// the "of M placed" figure into something no amount of placing could satisfy.</summary>
        private static int RequiredFor(string specialDevice, BomExtras extras)
        {
            if (extras.SubsystemDemands == null) return 0;

            int required = 0;
            foreach (var demand in extras.SubsystemDemands)
            {
                if (demand == null || !string.Equals(demand.Subsystem, specialDevice,
                                                     StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var part in demand.Parts)
                    if (part != null && part.Mount == DemandMount.LvCompartment)
                        required += part.Quantity;
            }
            return required;
        }

        /// <summary>
        /// Processors implied by the job's control-link demand.
        ///
        /// Delegates to <see cref="ControlLinkPacker"/>, which is also what fills the Panel
        /// Breakdown's capacity bars. It used to compute this itself, by pooling every device in the
        /// job and dividing by the link cap — which assumes a panel's modules can be split across two
        /// links. They cannot, so the pooled figure could only ever come in at or below the truth,
        /// and it disagreed with the bars that the designer was reading at the same moment.
        /// </summary>
        public static int CalculateRecommendedProcessors(
            List<PanelResult> allPanels, BomExtras extras, BrandConfig brand = null)
            => ControlLinkPacker.RecommendProcessors(
                ControlLinkPacker.BuildDemand(allPanels, extras, brand));

        /// <summary>
        /// How many QS-link power supplies (QSPS-DH-1-75-H) the job needs, and whether the placed
        /// panels can physically hold them.
        ///
        /// <b>PDU nets per link, pooled within it.</b> A power group cannot span a link (V+ is link
        /// wiring), so supplies are sized one link at a time: each supply gives +75, and the devices on
        /// the link draw against it. The packer already distributes the device draws
        /// (<see cref="PackedLink.ConsumedPdu"/>); this adds the one draw it cannot — the processor's
        /// −8, which is per-<i>processor</i> and lands on that processor's <b>first QS link</b> (not
        /// "the first N QS links", or a processor heading two QS links would be billed −16 and its
        /// neighbour nothing). Then <c>ceil(|net| / 75)</c> per QS link, summed.
        ///
        /// The <b>all-wireless-processor safeguard</b>: a processor whose two links both went to Clear
        /// Connect has no QS link to carry its −8, yet the box still needs power — so each such
        /// processor adds one supply directly, matching the one-per-processor floor the old code had.
        ///
        /// Feasibility is <b>global</b>: total supplies vs the sum of
        /// <see cref="BrandConfig.PowerSupplyCapacityByPanelSize"/> across every placed panel. There is
        /// no physical panel→link assignment to check against — a panel is grouped spatially and the
        /// pack's panel→link scatter is a recommendation — so a per-link check would false-alarm on a
        /// keypad-heavy link the FFD left with no panel.
        /// </summary>
        internal static PowerSupplySizing SizePowerSupplies(
            List<PanelResult> allPanels, BrandConfig brand, BomExtras extras)
        {
            int slots = allPanels.Sum(p => brand.GetPowerSupplySlots(p.PanelCapacity));

            // Per-SLOT, matching the processor line (CountPlacedSpecialDevice): each "Processor"
            // selection is one HQP7-2 and its own two links, so an LV21 with a processor in each of its
            // two compartments is two processors → four links → two supplies (each processor is its own
            // power group and cannot share a QSPS). Per-panel counting would order both HQP7-2s but only
            // one supply. The sidebar bars count the same way (LinkAssignmentService builds one
            // ProcessorInstance per slot), so the two supplies and the four bars agree by construction.
            int processorCount = CountPlacedSpecialDevice(allPanels, "Processor");
            if (processorCount == 0)
                return new PowerSupplySizing(0, slots);

            int supplyPdu = brand.PowerSupplyPdu > 0 ? brand.PowerSupplyPdu : 75;

            int availableLinks = processorCount * ControlLinkPacker.LinksPerProcessor;
            var links = ControlLinkPacker.Pack(
                ControlLinkPacker.BuildDemand(allPanels, extras, brand), availableLinks).Links;

            // Per-QS-link draw magnitude the packer distributed. Clear Connect links carry no PDU.
            var draw = new int[links.Count];
            for (int i = 0; i < links.Count; i++)
                draw[i] = links[i].IsClearConnect ? 0 : Math.Abs(links[i].ConsumedPdu);

            // Charge each processor's −8 to its first QS link; an all-wireless processor has none, so it
            // takes a supply on its own account.
            int processorDraw = Math.Abs(brand.GetDevicePduDraw("Processor"));
            int allWirelessProcessors = 0;
            for (int j = 0; j < processorCount; j++)
            {
                int firstQs = -1;
                for (int k = 0; k < ControlLinkPacker.LinksPerProcessor; k++)
                {
                    int idx = j * ControlLinkPacker.LinksPerProcessor + k;
                    if (idx < links.Count && !links[idx].IsClearConnect) { firstQs = idx; break; }
                }
                if (firstQs < 0) allWirelessProcessors++;
                else draw[firstQs] += processorDraw;
            }

            int quantity = allWirelessProcessors;
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].IsClearConnect || draw[i] <= 0) continue;
                quantity += (int)Math.Ceiling((double)draw[i] / supplyPdu);
            }

            return new PowerSupplySizing(quantity, slots);
        }

        /// <summary>Counts how many compartment slots across all panels hold the named device.</summary>
        private static int CountPlacedSpecialDevice(List<PanelResult> allPanels, string deviceName)
        {
            int count = 0;
            foreach (var panel in allPanels)
            {
                foreach (string selected in panel.CompartmentSlots)
                {
                    if (string.Equals(selected, deviceName, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
            }
            return count;
        }
    }

    /// <summary>How many QS-link power supplies a job needs, and how many the placed panels can hold —
    /// the two numbers the global feasibility warning compares.</summary>
    internal readonly struct PowerSupplySizing
    {
        public PowerSupplySizing(int quantity, int slotsAvailable)
        {
            Quantity = quantity;
            SlotsAvailable = slotsAvailable;
        }

        /// <summary>Supplies to order.</summary>
        public int Quantity { get; }

        /// <summary>Supply positions the panel mix provides — Σ PowerSupplyCapacityByPanelSize.</summary>
        public int SlotsAvailable { get; }
    }

    /// <summary>
    /// The BOM inputs that do not come from the panel allocation — project-wide device counts plus
    /// who the list is for.
    /// </summary>
    public class BomExtras
    {
        public int KeypadCount { get; set; }
        public int TwoGangKeypadCount { get; set; }

        /// <summary>The same keypads counted for ordering — grouped by catalog number rather than by
        /// gang and radio. Not derivable from the counts above, nor they from these: gang doubles a
        /// device but not an order line, and radio decides which link but not which part.</summary>
        public IReadOnlyList<ControlDeviceTally> KeypadTallies { get; set; }

        /// <summary>Hybrid Repeaters: devices on the link, and the parts to order for them.</summary>
        public ControlDeviceGroup HybridRepeaters { get; set; }

        public IReadOnlyList<ControlDeviceTally> HybridRepeaterTallies => HybridRepeaters?.Tallies;

        /// <summary>Repeaters on the job, for the Clear Connect link math. Comes from the instance
        /// count, <b>not</b> from summing the order rows: a repeater type declaring a mounting bracket
        /// in a second catalog slot orders two parts and is still one device, and summing would
        /// silently size the wireless links for hardware that does not exist.</summary>
        public int HybridRepeaterCount => HybridRepeaters?.DeviceCount ?? 0;

        /// <summary>Wireless devices, already expanded to device count. These ride the processor's
        /// Clear Connect link rather than a QS link, so they pressure a different budget — and a job
        /// with enough of them needs another Clear Connect link, which comes out of a processor's
        /// pair of two exactly as the repeaters' do.</summary>
        public int WirelessDeviceCount { get; set; }

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
