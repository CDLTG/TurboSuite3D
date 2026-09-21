#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// The one computation of control-link demand: how a job's devices and switch legs pack onto
    /// processor links, and therefore how many processors it takes.
    ///
    /// <b>Why one.</b> This question used to be answered by two different algorithms that could not
    /// agree, because neither knew the other existed:
    /// <list type="bullet">
    /// <item><c>ControlBomBuilder.CalculateRecommendedProcessors</c> POOLED every device in the job
    /// and divided by the cap — which quietly assumes a panel's modules can be split across two
    /// links. They cannot; a panel is wired to one link.</item>
    /// <item><c>LinkAssignmentService</c> bin-packed, but forward-only: once it advanced past a link
    /// it never came back, so it could strand capacity on an early link and redden a later one on a
    /// job that fits.</item>
    /// </list>
    /// Both are replaced by this. The Panel Breakdown's capacity bars and the BOM's processor
    /// recommendation are now the same function asked two questions, so the invariant that matters
    /// holds by construction: <b>if a bar is over capacity, the BOM recommends more processors, and
    /// if the BOM recommends more processors, some bar is over capacity.</b>
    ///
    /// <b>Nothing here is a user choice.</b> The Panel Breakdown is a recommendation surface: the
    /// designer picks panel sizes and sites processors, I/O and interfaces, and everything else is
    /// derived. There is deliberately no way to assign a panel to a specific link — if the derived
    /// layout does not fit, the answer is another processor, and the bars are how that is said.
    /// Downstream, the real design is imported into Lutron's own software, which is why the BOM
    /// carries "Verify bill of materials with official control system documentation."
    ///
    /// <b>Third dimension: PDU.</b> The QS Link Power Supply budget rides here, but only the part that
    /// is a packing fact: each unit and each poured keypad carries a signed <see cref="LinkUnit.Pdu"/>,
    /// the bins sum it, and every <see cref="PackedLink"/> reports a <see cref="PackedLink.ConsumedPdu"/>
    /// — the per-link device draw the packer already distributes (keypad −1, QSE-IO −3, QSE-CI-DMX −2;
    /// modules and panels 0). What is <i>not</i> here is the processor's −8 and the supply sizing: −8 is
    /// per-<i>processor</i>, not per-link, and this packer takes an <c>int availableLinks</c>, not
    /// "processor A's two links", so it cannot bill it; and feasibility is a <b>global</b> slot check,
    /// not per-link, because panel→link is a recommendation rather than physical wiring. Both live in
    /// <c>ControlBomBuilder</c>'s supply sizer, the sole consumer of <see cref="PackedLink.ConsumedPdu"/>.
    /// Clear Connect links carry no PDU budget by product architecture, so their <see cref="PackedLink.ConsumedPdu"/>
    /// is always 0.
    /// </summary>
    public static class ControlLinkPacker
    {
        /// <summary>Links on a HomeWorks QSX processor (HQP7-2 — the "-2" is this number).</summary>
        public const int LinksPerProcessor = 2;

        /// <summary>
        /// Packs the demand onto links.
        ///
        /// <paramref name="availableLinks"/> null means "open as many links as it takes" — the
        /// question <see cref="RecommendProcessors"/> asks. A number means "these are the links that
        /// exist" — the question the capacity bars ask, where work that does not fit lands on the
        /// emptiest link and shows as over capacity rather than disappearing.
        ///
        /// When constrained, the result always has exactly <paramref name="availableLinks"/> entries,
        /// QS first and Clear Connect last, so a caller can map them positionally onto Link 1, Link 2,
        /// Link 3… and get the trailing-links-go-wireless behaviour for free.
        /// </summary>
        public static LinkPackResult Pack(LinkDemand? demand, int? availableLinks = null)
        {
            demand ??= new LinkDemand();
            bool unlimited = !availableLinks.HasValue;
            int budget = unlimited ? int.MaxValue : Math.Max(0, availableLinks.GetValueOrDefault());

            bool hasQsWork = demand.PinnedUnits.Count > 0
                             || demand.FloatingUnits.Count > 0
                             || demand.FloatingDevices > 0
                             || demand.FloatingLoads > 0;

            int ccaLinks = ClearConnectLinksFor(
                demand.RepeaterCount, demand.WirelessDevices, budget, hasQsWork, unlimited);
            int qsLinks = unlimited ? 0 : Math.Max(0, budget - ccaLinks);

            var qsBins = new List<Bin>();
            for (int i = 0; i < qsLinks; i++)
                qsBins.Add(new Bin());

            // First-fit DECREASING over the indivisible units — biggest first. A panel is one unit:
            // its modules, plus any compartment device sited in it, all ride the same link.
            foreach (var unit in Ordered(demand.PinnedUnits))
                Place(unit, qsBins, unlimited);

            // Then the units nobody has sited yet — an interface the solve says the job needs but
            // that has not been dropped into a compartment. Indivisible like a panel, but free to
            // land anywhere, so it packs after the things whose home is already decided.
            foreach (var unit in Ordered(demand.FloatingUnits))
                Place(unit, qsBins, unlimited);

            // Finally the genuinely divisible demand: keypads are one device each and go wherever
            // there is room, so they fill the gaps the units left rather than forcing new links. Their
            // PDU rides the pour and lands per bin, so the sizer sees each link's true keypad draw.
            Pour(demand.FloatingDevices, qsBins, unlimited, asDevices: true, totalPdu: demand.FloatingDevicePdu);
            Pour(demand.FloatingLoads, qsBins, unlimited, asDevices: false);

            var links = qsBins.Select(b => b.ToQsLink()).ToList();
            links.AddRange(PackWireless(demand.RepeaterCount, demand.WirelessDevices, ccaLinks));

            return new LinkPackResult(links, qsBins.Count, ccaLinks);
        }

        /// <summary>
        /// Processors the job needs: pack into as many links as it takes, then divide by the two a
        /// processor carries. Never returns less than 1 — a job with no demand at all still needs a
        /// processor to be a system.
        /// </summary>
        public static int RecommendProcessors(LinkDemand? demand)
        {
            var packed = Pack(demand, availableLinks: null);
            int links = Math.Max(1, packed.TotalLinkCount);
            return Math.Max(1, (int)Math.Ceiling((double)links / LinksPerProcessor));
        }

        /// <summary>
        /// The constrained/arrange mode: packs the demand onto the links a fixed set of placed
        /// processors provides, arranged by the firm conventions (rules #2–#4). Distinct from the flat
        /// <see cref="Pack(LinkDemand, int?)"/> overload — this one is <b>location-aware</b>: it pools a
        /// location's indivisible units onto a processor sharing that location, fans keypads onto a spare
        /// QS link, and reports the result <b>grouped per processor</b> so the sidebar reads Link 1 / Link
        /// 2 directly rather than mapping a flat list positionally.
        ///
        /// <b>Fit-preserving by construction.</b> Every preference falls back rather than overflowing: a
        /// located unit that does not fit its own-location processor spans to spare capacity on another
        /// (rule #3), and keypads collapse onto a shared link when no spare QS link exists (rule #2). So
        /// if the flat collapse fits in these processors, this arrangement fits too — the count never
        /// moves, only the arrangement within it. <see cref="RecommendProcessors"/> owns the count.
        ///
        /// The orphan-assignment map (item 5) never reaches here: it is applied as a
        /// <see cref="RelabelLocations"/> pre-pass that rewrites an orphan unit's location to its host's,
        /// so this method only ever knows one rule — "prefer a processor whose location matches; span if
        /// full."
        /// </summary>
        public static LinkPackResult Pack(LinkDemand? demand, IReadOnlyList<ProcessorSlot>? processors)
        {
            // No processor structure means the sizing question, which the flat overload answers.
            if (processors == null)
                return Pack(demand, availableLinks: null);

            demand ??= new LinkDemand();

            // The flat link-slot structure, processor-major. Clear Connect is carved off the TRAILING
            // positions (Gap #9): the last ccaLinks slots go RF, the rest are QS.
            var slots = new List<(int Proc, int LinkNumber, int Location)>();
            for (int p = 0; p < processors.Count; p++)
            {
                int linkCount = Math.Max(0, processors[p].LinkCount);
                for (int ln = 1; ln <= linkCount; ln++)
                    slots.Add((p, ln, processors[p].Location));
            }

            int totalLinks = slots.Count;
            bool hasQsWork = demand.PinnedUnits.Count > 0
                             || demand.FloatingUnits.Count > 0
                             || demand.FloatingDevices > 0
                             || demand.FloatingLoads > 0;

            int ccaLinks = ClearConnectLinksFor(
                demand.RepeaterCount, demand.WirelessDevices, totalLinks, hasQsWork, unlimited: false);
            int firstCcaSlot = totalLinks - ccaLinks;   // slots at or after this index go RF

            // One QS bin per QS slot, carrying its processor/link identity for deterministic tiebreaks.
            var qsBins = new List<Bin>();
            var binBySlot = new Dictionary<int, Bin>();
            for (int s = 0; s < firstCcaSlot; s++)
            {
                var bin = new Bin { ProcIndex = slots[s].Proc, LinkNumber = slots[s].LinkNumber, Location = slots[s].Location };
                qsBins.Add(bin);
                binBySlot[s] = bin;
            }

            // 1) Located, indivisible units (dimmer + shade panels) — pool onto a QS link in their own
            //    location; span to spare capacity elsewhere if their pool is full (never add a link).
            var located = Ordered(demand.PinnedUnits.Where(u => u.Location > 0)).ToList();
            foreach (int location in located.Select(u => u.Location).Distinct().OrderBy(n => n))
            {
                var localBins = qsBins.Where(b => b.Location == location)
                    .OrderBy(b => b.ProcIndex).ThenBy(b => b.LinkNumber).ToList();
                foreach (var unit in located.Where(u => u.Location == location))
                    PlaceLocated(unit, localBins, qsBins);
            }

            // 2) Location-less indivisible units (a dimmer panel whose name carries no location, a
            //    floating interface) — no pooling preference, first-fit into spare capacity.
            foreach (var unit in Ordered(demand.PinnedUnits.Where(u => u.Location <= 0)))
                Place(unit, qsBins, unlimited: false);
            foreach (var unit in Ordered(demand.FloatingUnits))
                Place(unit, qsBins, unlimited: false);

            // 3) Keypads (QS-only) pour last, isolated onto a located-unit-free QS link where one exists,
            //    else collapsing onto shared gaps (the text-block case). Never a Clear Connect link.
            PourKeypads(demand.FloatingDevices, qsBins, demand.FloatingDevicePdu);
            PourKeypads(demand.FloatingLoads, qsBins, totalPdu: 0, asDevices: false);

            // 4) Clear Connect links, assigned back to their carved trailing slots.
            var ccaList = PackWireless(demand.RepeaterCount, demand.WirelessDevices, ccaLinks).ToList();

            var bySlot = new PackedLink[totalLinks];
            for (int s = 0; s < firstCcaSlot; s++)
                bySlot[s] = binBySlot[s].ToQsLink();
            for (int i = 0; i < ccaList.Count; i++)
                bySlot[firstCcaSlot + i] = ccaList[i];

            // Per-processor grouping, positional to the input list.
            var groups = new List<ProcessorGroup>();
            int slotCursor = 0;
            for (int p = 0; p < processors.Count; p++)
            {
                int linkCount = Math.Max(0, processors[p].LinkCount);
                PackedLink link1 = linkCount >= 1 ? bySlot[slotCursor] : EmptyQsLink();
                PackedLink link2 = linkCount >= 2 ? bySlot[slotCursor + 1] : EmptyQsLink();
                groups.Add(new ProcessorGroup(processors[p].Location, link1, link2));
                slotCursor += linkCount;
            }

            // Flat list: QS first, Clear Connect last — the shape the flat overload produces, so counting
            // and any positional back-compat are unchanged.
            var flat = new List<PackedLink>();
            for (int s = 0; s < firstCcaSlot; s++) flat.Add(bySlot[s]);
            flat.AddRange(ccaList);

            return new LinkPackResult(flat, qsBins.Count, ccaLinks, groups);
        }

        private static PackedLink EmptyQsLink()
            => new PackedLink(ProcessorLink.QsLinkType, 0, 0, System.Array.Empty<string>());

        /// <summary>
        /// Rewrites units' locations by the orphan-assignment map (item 5) before a pooling pack — an
        /// orphan location (panels but no processor) is relabelled to the processor-bearing location the
        /// designer assigned it to, so its panels pool there. Pure: returns a new demand, so
        /// <see cref="Pack(LinkDemand, IReadOnlyList{ProcessorSlot})"/> never learns the word "orphan".
        /// A location absent from the map is left as-is (self-pools, or floats when no processor matches).
        /// </summary>
        public static LinkDemand RelabelLocations(
            LinkDemand demand, IReadOnlyDictionary<int, int>? orphanToHost)
        {
            if (demand == null) return new LinkDemand();
            if (orphanToHost == null || orphanToHost.Count == 0) return demand;

            LinkUnit Relabel(LinkUnit u) =>
                u.Location > 0 && orphanToHost.TryGetValue(u.Location, out int host) && host != u.Location
                    ? u.WithLocation(host)
                    : u;

            return new LinkDemand(
                demand.PinnedUnits.Select(Relabel).ToList(),
                demand.FloatingUnits.Select(Relabel).ToList(),
                demand.FloatingDevices, demand.FloatingLoads,
                demand.RepeaterCount, demand.WirelessDevices, demand.FloatingDevicePdu);
        }

        /// <summary>
        /// Turns a panel allocation plus the job's non-panel inputs into link demand. Shared by both
        /// questions on purpose — a divergence between the bars and the recommendation could
        /// otherwise creep back in through the inputs rather than the algorithm.
        /// </summary>
        public static LinkDemand BuildDemand(
            IEnumerable<PanelResult>? allPanels, BomExtras? extras, BrandConfig? brand = null)
        {
            var panels = allPanels?.ToList() ?? new List<PanelResult>();
            extras ??= new BomExtras();

            var demands = (extras.SubsystemDemands ?? new List<ControlSubsystemDemand>())
                .Where(d => d != null)
                .ToList();

            // Names a subsystem speaks for with a compartment part. Those slots are accounted below,
            // where the subsystem's own device and leg budgets are known; counting them here as well
            // would charge the link twice for one interface.
            var subsystemNames = new HashSet<string>(
                demands.Where(d => CompartmentQuantity(d) > 0).Select(d => d.Subsystem),
                StringComparer.OrdinalIgnoreCase);

            var panelDevices = new Dictionary<PanelResult, int>();
            var panelLoads = new Dictionary<PanelResult, int>();

            // Signed PDU each panel's LinkUnit carries — the sum of the V+ draws of the interfaces sited
            // in its compartments. Modules and the panel itself draw nothing (see BrandConfig), so this
            // stays 0 unless a compartment device lands. Null brand ⇒ every draw is 0, which is what the
            // capacity bars and the processor recommendation want: neither reads PDU.
            var panelPdu = new Dictionary<PanelResult, int>();
            foreach (var panel in panels)
            {
                panelDevices[panel] = panel.DeviceCount;
                panelLoads[panel] = panel.LoadCount;
                panelPdu[panel] = 0;

                foreach (string slot in panel.CompartmentSlots)
                {
                    if (!IsDeviceSelection(slot) || subsystemNames.Contains(slot))
                        continue;

                    // A compartment device nobody speaks for — QSE-IO, or a QSE-CI-DMX on a job where
                    // TurboDMX has nothing to say. One QS device, its nameplate switch legs (QSE-IO → 5;
                    // a silent QSE-CI-DMX contributes none until its subsystem reports channels), its own
                    // V+ draw. Legs are real load-bar demand, so — unlike PDU — every caller passes brand
                    // and these show on the bars too; a null brand (a brand with no leg model) is 0.
                    panelDevices[panel] += 1;
                    panelLoads[panel] += brand?.GetDeviceSwitchLegs(slot) ?? 0;
                    panelPdu[panel] += brand?.GetDevicePduDraw(slot) ?? 0;
                }
            }

            var floatingUnits = new List<LinkUnit>();

            // Located, indivisible units a subsystem reports as physical panels rather than a divisible
            // pour — shade panels (QSPS-10PNL), one per unit. They pool by location exactly like dimmer
            // panels, so they join the pinned units below.
            var subsystemUnits = new List<LinkUnit>();

            // Wired keypads only. A wireless one is not a QS device at all — it rides the Clear
            // Connect link, and pouring it in here would charge a link that never sees it while
            // leaving the link that does under-reported.
            int floatingKeypadDevices = extras.KeypadCount + extras.TwoGangKeypadCount * 2;
            int floatingDevices = floatingKeypadDevices;
            int floatingLoads = 0;

            // Keypad PDU rides the pour. Today every floating device IS a keypad (the only
            // compartment-less demand, a future DALI DIN module, is benched and emits none), so this
            // total distributes exactly at −1 per device; when a compartment-less subsystem with its
            // own draw lands it must be poured separately rather than folded in here (Phase 3).
            int keypadPdu = brand?.GetDevicePduDraw("Keypad") ?? 0;
            int floatingDevicePdu = floatingKeypadDevices * keypadPdu;

            foreach (var demand in demands)
            {
                int required = CompartmentQuantity(demand);
                if (required <= 0)
                {
                    // A subsystem reporting located physical panels (shades) — pack each whole, pooled by
                    // location, like a dimmer panel. The units carry the same budget the aggregate would
                    // pour, so it is one or the other, never both.
                    if (demand.LinkUnits.Count > 0)
                    {
                        var category = CategoryForSubsystem(demand.Subsystem);
                        foreach (var u in demand.LinkUnits)
                            subsystemUnits.Add(new LinkUnit(
                                u.Name, u.Devices, u.Loads, pdu: 0, category, u.Location));
                        continue;
                    }

                    // A subsystem with no compartment part and no located units — a future DALI DIN
                    // module, or a demand that is pure link budget. Nothing pins it to a panel, and it is
                    // many small devices rather than one big one, so it pours like keypads do.
                    floatingDevices += demand.LinkDevices;
                    floatingLoads += demand.LinkLoads;
                    continue;
                }

                // Where the designer has sited interfaces, the link cost sits with them; the rest
                // floats. The demand only carries job totals (N interfaces, T legs) with no per-unit
                // breakdown — and cannot carry one, since nothing associates a solved loop with a
                // compartment — so the budgets are split evenly across the required interfaces.
                // Interfaces are interchangeable to the link math, so this loses nothing real.
                var sited = SitedSlots(panels, demand.Subsystem);
                int[] deviceShares = Split(demand.LinkDevices, required);
                int[] loadShares = Split(demand.LinkLoads, required);

                // Every sited interface is a physical QSE-CI-DMX drawing V+, whether or not it carries
                // legs of its own — so its PDU lands here, on the panel it sits in, for all of them.
                int subsystemPdu = brand?.GetDevicePduDraw(demand.Subsystem) ?? 0;
                foreach (var panel in sited)
                    panelPdu[panel] += subsystemPdu;

                for (int i = 0; i < required; i++)
                {
                    if (i < sited.Count)
                    {
                        panelDevices[sited[i]] += deviceShares[i];
                        panelLoads[sited[i]] += loadShares[i];
                    }
                    else
                    {
                        floatingUnits.Add(new LinkUnit(
                            demand.Subsystem + " interface", deviceShares[i], loadShares[i], subsystemPdu,
                            LinkCategory.Interface));
                    }
                }

                // Sited beyond the requirement: the designer put down more interfaces than the solve
                // asked for. They still occupy the link, they just carry no legs of their own. (Their
                // V+ draw is already counted in the panelPdu loop above.)
                for (int i = required; i < sited.Count; i++)
                    panelDevices[sited[i]] += 1;
            }

            var pinned = panels
                .Where(p => panelDevices[p] > 0 || panelLoads[p] > 0)
                .Select(p => new LinkUnit(p.PanelName, panelDevices[p], panelLoads[p], panelPdu[p],
                    LinkCategory.Modules, PanelAllocationService.ParseLocationNumber(p.PanelName)))
                .ToList();

            // Shade panels join the pinned units — located and indivisible, they pool exactly as the
            // dimmer panels do.
            pinned.AddRange(subsystemUnits);

            return new LinkDemand(pinned, floatingUnits, floatingDevices, floatingLoads,
                extras.HybridRepeaterCount, extras.WirelessDeviceCount, floatingDevicePdu);
        }

        /// <summary>Compartment slots across all panels holding the named device, in panel order.</summary>
        internal static List<PanelResult> SitedSlots(IEnumerable<PanelResult> panels, string deviceName)
        {
            var sited = new List<PanelResult>();
            foreach (var panel in panels)
            {
                foreach (string slot in panel.CompartmentSlots)
                {
                    if (string.Equals(slot, deviceName, StringComparison.OrdinalIgnoreCase))
                        sited.Add(panel);
                }
            }
            return sited;
        }

        /// <summary>A compartment selection that is an actual device — not blank, not "Empty", and not
        /// the processor, which is the head end of its links rather than something on them.</summary>
        internal static bool IsDeviceSelection(string? selection)
            => !string.IsNullOrEmpty(selection)
               && !string.Equals(selection, "Empty", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(selection, "Processor", StringComparison.OrdinalIgnoreCase);

        /// <summary>The category a subsystem's located units carry. Shades today; a future located
        /// subsystem adds its case here.</summary>
        private static LinkCategory CategoryForSubsystem(string subsystem)
            => string.Equals(subsystem, ShadeSolver.SubsystemName, StringComparison.OrdinalIgnoreCase)
                ? LinkCategory.Shades
                : LinkCategory.None;

        private static int CompartmentQuantity(ControlSubsystemDemand demand)
            => demand.Parts
                .Where(p => p != null && p.Mount == DemandMount.LvCompartment)
                .Sum(p => p.Quantity);

        /// <summary>
        /// Clear Connect links the wireless side needs. Wireless takes links off the top and never
        /// shares with QS — one repeater converts a whole link, which is why a job that would run on
        /// one processor can need two purely to carry wireless.
        ///
        /// <b>Two caps, not one.</b> A CC-A link holds four repeaters AND 99 devices, and either can
        /// bind: four repeaters serving ninety wireless keypads is one link on the repeater count and
        /// one on the device count, but bump the keypads past 99 and a second link is needed even
        /// though the repeaters still fit. Reading the repeater cap as a device cap — which an earlier
        /// cut of this did — makes every wireless device past the fourth look like an overflow.
        ///
        /// <b>A third cap exists and is not a term here yet:</b> 100 switch legs
        /// (<see cref="ProcessorLink.MaxClearConnectLoads"/>). Nothing produces one — a wireless keypad
        /// is a control, not an output, and wireless dimmers, shades and Sivoia drives are not
        /// collected. The capacity is declared so the bar and the over-capacity flag are right the
        /// moment one appears; a <c>ceil(wirelessLoads / 100)</c> term belongs here when there is
        /// something to put in it, and not before.
        ///
        /// Wireless devices with no repeater modelled still get a link: they have to live somewhere,
        /// and a repeater bar reading <c>0 / 4</c> is how the missing repeater becomes visible.
        ///
        /// When the link budget is fixed, CC-A stops one short of consuming every link that has QS
        /// work to do. Otherwise a processor with five repeaters would show two full CC-A links and
        /// no home at all for its panels — technically the truth, but it hides the panels instead of
        /// showing the problem. Leaving one QS link puts the overflow on a visibly over-capacity CC-A
        /// bar, which reads as "out of links, add a processor" — and the recommendation, which packs
        /// unconstrained, says exactly that.
        /// </summary>
        private static int ClearConnectLinksFor(
            int repeaters, int wirelessDevices, int budget, bool hasQsWork, bool unlimited)
        {
            if (repeaters <= 0 && wirelessDevices <= 0) return 0;

            int byRepeaters = (int)Math.Ceiling(
                (double)repeaters / ProcessorLink.MaxRepeatersPerClearConnectLink);
            int byDevices = (int)Math.Ceiling(
                (double)(repeaters + wirelessDevices) / ProcessorLink.MaxDevices);

            int needed = Math.Max(1, Math.Max(byRepeaters, byDevices));
            if (unlimited) return needed;

            int allowed = hasQsWork ? budget - 1 : budget;
            return Math.Max(0, Math.Min(needed, allowed));
        }

        private static IEnumerable<PackedLink> PackWireless(
            int repeaters, int wirelessDevices, int ccaLinks)
        {
            var bins = new List<Bin>();
            for (int i = 0; i < ccaLinks; i++)
                bins.Add(new Bin());
            if (bins.Count == 0)
                yield break;

            // Repeaters first, four per link — they are what makes a link a Clear Connect link at all,
            // and their cap is the tighter one.
            var repeatersPerBin = new int[bins.Count];
            int remainingRepeaters = repeaters;
            for (int i = 0; i < bins.Count && remainingRepeaters > 0; i++)
            {
                int take = Math.Min(remainingRepeaters, ProcessorLink.MaxRepeatersPerClearConnectLink);
                repeatersPerBin[i] = take;
                bins[i].Devices += take;
                remainingRepeaters -= take;
            }

            // More repeaters than the links can hold — pile the rest on the last one so the bar shows
            // it. Only reachable when the link budget is fixed.
            if (remainingRepeaters > 0)
            {
                repeatersPerBin[bins.Count - 1] += remainingRepeaters;
                bins[bins.Count - 1].Devices += remainingRepeaters;
            }

            // Then the wireless devices those repeaters exist to serve, into whatever device room is
            // left. They place one at a time, so they pour rather than pack.
            int remainingDevices = wirelessDevices;
            for (int i = 0; i < bins.Count && remainingDevices > 0; i++)
            {
                int room = ProcessorLink.MaxDevices - bins[i].Devices;
                if (room <= 0) continue;
                int take = Math.Min(room, remainingDevices);
                bins[i].Devices += take;
                remainingDevices -= take;
            }
            if (remainingDevices > 0)
                bins[bins.Count - 1].Devices += remainingDevices;

            for (int i = 0; i < bins.Count; i++)
                yield return new PackedLink(
                    ProcessorLink.ClearConnectLinkType, bins[i].Devices, 0, bins[i].UnitNames,
                    repeaters: repeatersPerBin[i]);
        }

        /// <summary>Biggest first — the "decreasing" in first-fit decreasing. Ties break on the name so
        /// the same job always packs the same way and the bars do not shuffle between rebuilds.</summary>
        private static IEnumerable<LinkUnit> Ordered(IEnumerable<LinkUnit> units)
            => units.OrderByDescending(u => u.Devices)
                    .ThenByDescending(u => u.Loads)
                    .ThenBy(u => u.Name, StringComparer.Ordinal);

        private static void Place(LinkUnit unit, List<Bin> bins, bool unlimited)
        {
            foreach (var bin in bins)
            {
                if (bin.Fits(unit))
                {
                    bin.Add(unit);
                    return;
                }
            }

            if (unlimited)
            {
                var fresh = new Bin();
                fresh.Add(unit);
                bins.Add(fresh);
                return;
            }

            // No link exists at all (every one went to Clear Connect, or no processor is sited).
            // Nothing to show it on.
            if (bins.Count == 0) return;

            // The links that exist cannot hold it. Land it on the emptiest so the overflow is visible.
            Emptiest(bins).Add(unit);
        }

        private static void Pour(
            int amount, List<Bin> bins, bool unlimited, bool asDevices, int totalPdu = 0)
        {
            if (amount <= 0) return;

            // PDU follows the devices as they land. Running-remainder against the original amount, so
            // each bin's share is the exact integer prefix difference: with a uniform rate (every
            // floating device is a keypad at −1) this is exact per bin, and it always sums back to
            // totalPdu regardless. Loads carry no PDU.
            int total = amount;
            int placed = 0, placedPdu = 0;
            int PduFor(int take)
            {
                if (!asDevices || totalPdu == 0 || total == 0) return 0;
                placed += take;
                int target = (int)((long)totalPdu * placed / total);
                int chunk = target - placedPdu;
                placedPdu = target;
                return chunk;
            }

            foreach (var bin in bins)
            {
                if (amount <= 0) break;
                int room = asDevices ? bin.DeviceRoom : bin.LoadRoom;
                if (room <= 0) continue;
                int take = Math.Min(room, amount);
                bin.Add(asDevices ? take : 0, asDevices ? 0 : take, PduFor(take));
                amount -= take;
            }

            while (amount > 0 && unlimited)
            {
                var fresh = new Bin();
                bins.Add(fresh);
                int take = Math.Min(asDevices ? fresh.DeviceRoom : fresh.LoadRoom, amount);
                fresh.Add(asDevices ? take : 0, asDevices ? 0 : take, PduFor(take));
                amount -= take;
            }

            if (amount > 0 && bins.Count > 0)
                Emptiest(bins).Add(asDevices ? amount : 0, asDevices ? 0 : amount, PduFor(amount));
        }

        private static Bin Emptiest(List<Bin> bins)
            => bins.OrderByDescending(b => Math.Min(b.DeviceRoom, b.LoadRoom))
                   .ThenBy(b => b.ProcIndex).ThenBy(b => b.LinkNumber).First();

        /// <summary>A located unit prefers a QS link in its own location; if none there fits, it spans to
        /// spare capacity anywhere (rule #3), landing over-capacity on the emptiest only when the whole
        /// budget is full. Never opens a link — the budget is fixed.</summary>
        private static void PlaceLocated(LinkUnit unit, List<Bin> localBins, List<Bin> allQsBins)
        {
            foreach (var bin in localBins)
            {
                if (bin.Fits(unit)) { bin.Add(unit); return; }
            }
            Place(unit, allQsBins, unlimited: false);   // span, then emptiest — the collapse fallback
        }

        /// <summary>
        /// Keypads (or their loads) pour into the fixed QS budget, isolated onto located-unit-free links
        /// first (rule #2 best-practice) and collapsing onto shared gaps only when no spare QS link
        /// exists. Never opens a link and never touches Clear Connect. PDU rides the device pour so a
        /// per-link draw stays right.
        /// </summary>
        private static void PourKeypads(int amount, List<Bin> qsBins, int totalPdu, bool asDevices = true)
        {
            if (amount <= 0 || qsBins.Count == 0) return;

            var ordered = qsBins.OrderBy(b => b.ProcIndex).ThenBy(b => b.LinkNumber).ToList();
            var isolated = ordered.Where(b => b.LocatedUnits == 0);
            var shared = ordered.Where(b => b.LocatedUnits > 0);
            var targets = isolated.Concat(shared).ToList();   // spare QS links first, module links after

            int total = amount, placed = 0, placedPdu = 0;
            int PduFor(int take)
            {
                if (!asDevices || totalPdu == 0 || total == 0) return 0;
                placed += take;
                int target = (int)((long)totalPdu * placed / total);
                int chunk = target - placedPdu;
                placedPdu = target;
                return chunk;
            }

            foreach (var bin in targets)
            {
                if (amount <= 0) break;
                int room = asDevices ? bin.DeviceRoom : bin.LoadRoom;
                if (room <= 0) continue;
                int take = Math.Min(room, amount);
                bin.Add(asDevices ? take : 0, asDevices ? 0 : take, PduFor(take), LinkCategory.Keypads);
                amount -= take;
            }

            if (amount > 0)
                Emptiest(qsBins).Add(asDevices ? amount : 0, asDevices ? 0 : amount, PduFor(amount),
                    LinkCategory.Keypads);
        }

        /// <summary>Splits a total into <paramref name="parts"/> whole shares that sum back to it
        /// exactly — largest remainder, so the leftover lands on the first shares rather than
        /// vanishing to rounding.</summary>
        internal static int[] Split(int total, int parts)
        {
            var shares = new int[Math.Max(0, parts)];
            if (parts <= 0 || total <= 0) return shares;

            int each = total / parts;
            int remainder = total - each * parts;
            for (int i = 0; i < parts; i++)
                shares[i] = each + (i < remainder ? 1 : 0);
            return shares;
        }

        /// <summary>One link being filled. Capacity is the QS pair — Clear Connect links are packed
        /// separately, by repeater count, and never mix with QS work.</summary>
        private sealed class Bin
        {
            public int Devices;
            public int Loads;

            /// <summary>Signed V+ PDU drawn on this link — a subset of the story <see cref="Devices"/>
            /// tells, since a device draws PDU only if it takes bus power, and the counts are not the
            /// same (a keypad is 1 device / −1 PDU, a QSE-IO 1 device / −3 PDU). Not a capacity here:
            /// nothing bins against it — the BOM sizer reads it and sizes supplies.</summary>
            public int Pdu;
            public readonly List<string> UnitNames = new List<string>();

            /// <summary>Which categories landed here — for the per-link composition the one-line reads,
            /// and for the keypad-isolation "is this link free of located units" test.</summary>
            public readonly HashSet<LinkCategory> Categories = new HashSet<LinkCategory>();

            /// <summary>How many located (Modules/Shades) units landed here. A keypad prefers an
            /// emptiest link with none (isolation, rule #2).</summary>
            public int LocatedUnits;

            // Identity, set only by the pooling overload: which processor and link this bin is, and the
            // processor's location. The flat overload leaves these at their defaults.
            public int ProcIndex = -1;
            public int LinkNumber;
            public int Location;

            public int DeviceRoom => ProcessorLink.MaxDevices - Devices;
            public int LoadRoom => ProcessorLink.MaxLoads - Loads;

            public bool Fits(LinkUnit unit) => unit.Devices <= DeviceRoom && unit.Loads <= LoadRoom;

            public void Add(LinkUnit unit)
            {
                Devices += unit.Devices;
                Loads += unit.Loads;
                Pdu += unit.Pdu;
                if (unit.Category != LinkCategory.None)
                    Categories.Add(unit.Category);
                if (unit.Category == LinkCategory.Modules || unit.Category == LinkCategory.Shades)
                    LocatedUnits++;
                if (!string.IsNullOrEmpty(unit.Name))
                    UnitNames.Add(unit.Name!);
            }

            public void Add(int devices, int loads, int pdu = 0, LinkCategory category = LinkCategory.None)
            {
                Devices += devices;
                Loads += loads;
                Pdu += pdu;
                if (category != LinkCategory.None && (devices > 0 || loads > 0))
                    Categories.Add(category);
            }

            public PackedLink ToQsLink() => new PackedLink(
                ProcessorLink.QsLinkType, Devices, Loads, UnitNames, consumedPdu: Pdu,
                categories: Categories.ToList());
        }
    }

    /// <summary>
    /// Everything that consumes control-link capacity, sorted by how freely it can move.
    ///
    /// The distinction is the whole point: a panel cannot be split across two links, so it packs as
    /// an indivisible unit and can force a new link on its own. A keypad is one device that goes
    /// wherever there is room, so it fills gaps instead. Treating the first like the second is what
    /// let the old pooled arithmetic under-report.
    /// </summary>
    public sealed class LinkDemand
    {
        public LinkDemand(
            IReadOnlyList<LinkUnit>? pinnedUnits = null,
            IReadOnlyList<LinkUnit>? floatingUnits = null,
            int floatingDevices = 0,
            int floatingLoads = 0,
            int repeaterCount = 0,
            int wirelessDevices = 0,
            int floatingDevicePdu = 0)
        {
            PinnedUnits = pinnedUnits ?? new List<LinkUnit>();
            FloatingUnits = floatingUnits ?? new List<LinkUnit>();
            FloatingDevices = floatingDevices;
            FloatingLoads = floatingLoads;
            RepeaterCount = repeaterCount;
            WirelessDevices = wirelessDevices;
            FloatingDevicePdu = floatingDevicePdu;
        }

        /// <summary>Indivisible and already sited — panels, with whatever is in their compartments.</summary>
        public IReadOnlyList<LinkUnit> PinnedUnits { get; }

        /// <summary>Indivisible but not yet sited — an interface the solve requires that nobody has
        /// dropped into a compartment.</summary>
        public IReadOnlyList<LinkUnit> FloatingUnits { get; }

        /// <summary>Devices that place one at a time: keypads, and any subsystem whose demand has no
        /// compartment part to pin it to.</summary>
        public int FloatingDevices { get; }

        /// <summary>Switch legs with no unit of their own to ride.</summary>
        public int FloatingLoads { get; }

        /// <summary>Hybrid Repeaters. These never touch a QS link — they take Clear Connect links off
        /// the processor entirely.</summary>
        public int RepeaterCount { get; }

        /// <summary>Wireless devices the repeaters serve, already expanded to device count. They ride
        /// the Clear Connect links alongside the repeaters and consume the same 99-device budget —
        /// which is the budget the repeater cap of four is <i>not</i>.</summary>
        public int WirelessDevices { get; }

        /// <summary>Signed PDU the floating-device pour carries, in total — today entirely keypad draw
        /// (−1 each). Distributed across the links the keypads land on, exactly under that uniform rate.
        /// The pinned/floating <i>units</i> carry their own <see cref="LinkUnit.Pdu"/> instead.</summary>
        public int FloatingDevicePdu { get; }
    }

    /// <summary>One indivisible thing that must fit on a single link.</summary>
    public sealed class LinkUnit
    {
        public LinkUnit(string? name, int devices, int loads, int pdu = 0,
            LinkCategory category = LinkCategory.None, int location = 0)
        {
            Name = name;
            Devices = devices;
            Loads = loads;
            Pdu = pdu;
            Category = category;
            Location = location;
        }

        /// <summary>What it is, for the packed link's contents list — a panel name, or an interface.</summary>
        public string? Name { get; }

        public int Devices { get; }
        public int Loads { get; }

        /// <summary>Signed V+ PDU this unit draws — the sum of its sited interfaces' draws. 0 for a
        /// bare panel of modules, which take no bus power.</summary>
        public int Pdu { get; }

        /// <summary>What kind of work this unit is (rule #2 fan-out, one-line rendering). Packing-neutral
        /// — the count/fit never depend on it.</summary>
        public LinkCategory Category { get; }

        /// <summary>The location this unit pools onto (rule #4), from the panel name. 0 = location-less
        /// (a floating interface); such a unit places into spare capacity with no pooling preference.</summary>
        public int Location { get; }

        /// <summary>A copy of this unit relabelled onto a different pool — the orphan-assignment pre-pass
        /// (item 5), which rewrites an orphan unit's <see cref="Location"/> before the pack so the packer
        /// only ever knows "prefer a matching location", never the concept of "orphan".</summary>
        public LinkUnit WithLocation(int location)
            => new LinkUnit(Name, Devices, Loads, Pdu, Category, location);
    }

    /// <summary>A processor's two links after the pooling pack — positional to the input
    /// <see cref="ProcessorSlot"/> list, so <c>Processors[i]</c> is slot <c>i</c>'s pair. Clear Connect,
    /// carved off the trailing link positions, lands on <see cref="Link2"/> of the last processors.</summary>
    public sealed class ProcessorGroup
    {
        public ProcessorGroup(int location, PackedLink link1, PackedLink link2)
        {
            Location = location;
            Link1 = link1;
            Link2 = link2;
        }

        /// <summary>The location this processor sits in (0 when its panel name carries none).</summary>
        public int Location { get; }

        public PackedLink Link1 { get; }
        public PackedLink Link2 { get; }
    }

    /// <summary>How the demand landed. When packed against a fixed link budget, <see cref="Links"/>
    /// has exactly that many entries, QS first and Clear Connect last.</summary>
    public sealed class LinkPackResult
    {
        public LinkPackResult(IReadOnlyList<PackedLink> links, int qsLinkCount, int clearConnectLinkCount,
            IReadOnlyList<ProcessorGroup>? processors = null)
        {
            Links = links;
            QsLinkCount = qsLinkCount;
            ClearConnectLinkCount = clearConnectLinkCount;
            Processors = processors ?? System.Array.Empty<ProcessorGroup>();
        }

        public IReadOnlyList<PackedLink> Links { get; }
        public int QsLinkCount { get; }
        public int ClearConnectLinkCount { get; }
        public int TotalLinkCount => QsLinkCount + ClearConnectLinkCount;

        /// <summary>Per-processor grouping — populated only by the pooling overload
        /// (<see cref="ControlLinkPacker.Pack(LinkDemand, IReadOnlyList{ProcessorSlot})"/>). Empty from
        /// the flat overload, whose callers read <see cref="Links"/> positionally.</summary>
        public IReadOnlyList<ProcessorGroup> Processors { get; }
    }

    /// <summary>One placed processor, as the pooling pack sees it: where it is, and how many links it
    /// heads (a HQP7-2 has two). Built by <c>LinkAssignmentService</c> from the sited processor
    /// compartments, one per slot, with the location parsed from the panel name.</summary>
    public sealed class ProcessorSlot
    {
        public ProcessorSlot(int location, int linkCount = ControlLinkPacker.LinksPerProcessor)
        {
            Location = location;
            LinkCount = linkCount;
        }

        /// <summary>The location this processor sits in — 0 when its panel name has none. A located unit
        /// pools onto a processor sharing its location; keypads and location-less units ignore it.</summary>
        public int Location { get; }

        /// <summary>Links this processor heads. Always <see cref="ControlLinkPacker.LinksPerProcessor"/>
        /// (2) for the shipped HQP7-2; kept a field for the MDU processors and forward-compat.</summary>
        public int LinkCount { get; }
    }

    /// <summary>One link's contents after packing.</summary>
    public sealed class PackedLink
    {
        public PackedLink(string linkType, int devices, int loads, IReadOnlyList<string> unitNames,
            int repeaters = 0, int consumedPdu = 0, IReadOnlyList<LinkCategory>? categories = null)
        {
            LinkType = linkType;
            Devices = devices;
            Loads = loads;
            UnitNames = unitNames;
            Repeaters = repeaters;
            ConsumedPdu = consumedPdu;
            Categories = categories ?? System.Array.Empty<LinkCategory>();
        }

        public string LinkType { get; }

        /// <summary>Signed V+ PDU drawn on this link by the devices the packer distributed here — the
        /// input the BOM supply sizer nets against the +75 a supply gives. Excludes the processor's −8
        /// (billed per-processor, in the sizer). Always 0 on a Clear Connect link.</summary>
        public int ConsumedPdu { get; }

        /// <summary>Everything on the link, repeaters included — they are devices like anything else.</summary>
        public int Devices { get; }

        public int Loads { get; }

        /// <summary>How many of <see cref="Devices"/> are Hybrid Repeaters, which carry their own much
        /// lower cap. A subset, not a second population.</summary>
        public int Repeaters { get; }

        /// <summary>Which units landed here. Nothing renders this yet — it is what makes a packing
        /// decision explicable when one needs explaining.</summary>
        public IReadOnlyList<string> UnitNames { get; }

        /// <summary>Which categories ride this link (rule #2 arrangement, one-line composition). Empty on
        /// a link the flat/sizing pack produced, since nothing there reads it.</summary>
        public IReadOnlyList<LinkCategory> Categories { get; }

        public bool IsClearConnect
            => string.Equals(LinkType, ProcessorLink.ClearConnectLinkType, StringComparison.OrdinalIgnoreCase);

        public int DeviceCapacity => ProcessorLink.MaxDevices;

        public int LoadCapacity
            => IsClearConnect ? ProcessorLink.MaxClearConnectLoads : ProcessorLink.MaxLoads;

        public int RepeaterCapacity => ProcessorLink.MaxRepeatersPerClearConnectLink;

        public bool IsOverCapacity
            => Devices > DeviceCapacity || Loads > LoadCapacity || Repeaters > RepeaterCapacity;
    }
}
