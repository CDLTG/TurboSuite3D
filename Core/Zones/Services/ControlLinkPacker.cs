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
    /// <b>Next dimension: PDU.</b> The QS Link Power Supply budget is the obvious third budget to add
    /// beside devices and loads, but it is <i>not</i> shaped like them and should not be bolted on as
    /// a third parallel counter: PDU is <b>signed</b> (a supply contributes, a device draws) and is
    /// budgeted <b>per power group, not per link</b> — a link may hold several power groups,
    /// separated by not connecting V+. Adding it means a group layer under the link, not another int.
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
            // there is room, so they fill the gaps the units left rather than forcing new links.
            Pour(demand.FloatingDevices, qsBins, unlimited, asDevices: true);
            Pour(demand.FloatingLoads, qsBins, unlimited, asDevices: false);

            var links = qsBins
                .Select(b => new PackedLink(ProcessorLink.QsLinkType, b.Devices, b.Loads, b.UnitNames))
                .ToList();
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
        /// Turns a panel allocation plus the job's non-panel inputs into link demand. Shared by both
        /// questions on purpose — a divergence between the bars and the recommendation could
        /// otherwise creep back in through the inputs rather than the algorithm.
        /// </summary>
        public static LinkDemand BuildDemand(IEnumerable<PanelResult>? allPanels, BomExtras? extras)
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
            foreach (var panel in panels)
            {
                panelDevices[panel] = panel.DeviceCount;
                panelLoads[panel] = panel.LoadCount;

                foreach (string slot in panel.CompartmentSlots)
                {
                    if (!IsDeviceSelection(slot) || subsystemNames.Contains(slot))
                        continue;

                    // A compartment device nobody speaks for — QSE-IO, or a QSE-CI-DMX on a job where
                    // TurboDMX has nothing to say. One QS device, no switch legs.
                    panelDevices[panel] += 1;
                }
            }

            var floatingUnits = new List<LinkUnit>();

            // Wired keypads only. A wireless one is not a QS device at all — it rides the Clear
            // Connect link, and pouring it in here would charge a link that never sees it while
            // leaving the link that does under-reported.
            int floatingDevices = extras.KeypadCount + extras.TwoGangKeypadCount * 2;
            int floatingLoads = 0;

            foreach (var demand in demands)
            {
                int required = CompartmentQuantity(demand);
                if (required <= 0)
                {
                    // A subsystem with no compartment part — a future DALI DIN module, or a demand
                    // that is pure link budget. Nothing pins it to a panel, and it is many small
                    // devices rather than one big one, so it pours like keypads do.
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
                            demand.Subsystem + " interface", deviceShares[i], loadShares[i]));
                    }
                }

                // Sited beyond the requirement: the designer put down more interfaces than the solve
                // asked for. They still occupy the link, they just carry no legs of their own.
                for (int i = required; i < sited.Count; i++)
                    panelDevices[sited[i]] += 1;
            }

            var pinned = panels
                .Where(p => panelDevices[p] > 0 || panelLoads[p] > 0)
                .Select(p => new LinkUnit(p.PanelName, panelDevices[p], panelLoads[p]))
                .ToList();

            return new LinkDemand(pinned, floatingUnits, floatingDevices, floatingLoads,
                extras.HybridRepeaterCount, extras.WirelessDeviceCount);
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

        private static void Pour(int amount, List<Bin> bins, bool unlimited, bool asDevices)
        {
            if (amount <= 0) return;

            foreach (var bin in bins)
            {
                if (amount <= 0) break;
                int room = asDevices ? bin.DeviceRoom : bin.LoadRoom;
                if (room <= 0) continue;
                int take = Math.Min(room, amount);
                bin.Add(asDevices ? take : 0, asDevices ? 0 : take);
                amount -= take;
            }

            while (amount > 0 && unlimited)
            {
                var fresh = new Bin();
                bins.Add(fresh);
                int take = Math.Min(asDevices ? fresh.DeviceRoom : fresh.LoadRoom, amount);
                fresh.Add(asDevices ? take : 0, asDevices ? 0 : take);
                amount -= take;
            }

            if (amount > 0 && bins.Count > 0)
                Emptiest(bins).Add(asDevices ? amount : 0, asDevices ? 0 : amount);
        }

        private static Bin Emptiest(List<Bin> bins)
            => bins.OrderByDescending(b => Math.Min(b.DeviceRoom, b.LoadRoom)).First();

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
            public readonly List<string> UnitNames = new List<string>();

            public int DeviceRoom => ProcessorLink.MaxDevices - Devices;
            public int LoadRoom => ProcessorLink.MaxLoads - Loads;

            public bool Fits(LinkUnit unit) => unit.Devices <= DeviceRoom && unit.Loads <= LoadRoom;

            public void Add(LinkUnit unit)
            {
                Devices += unit.Devices;
                Loads += unit.Loads;
                if (!string.IsNullOrEmpty(unit.Name))
                    UnitNames.Add(unit.Name!);
            }

            public void Add(int devices, int loads)
            {
                Devices += devices;
                Loads += loads;
            }
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
            int wirelessDevices = 0)
        {
            PinnedUnits = pinnedUnits ?? new List<LinkUnit>();
            FloatingUnits = floatingUnits ?? new List<LinkUnit>();
            FloatingDevices = floatingDevices;
            FloatingLoads = floatingLoads;
            RepeaterCount = repeaterCount;
            WirelessDevices = wirelessDevices;
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
    }

    /// <summary>One indivisible thing that must fit on a single link.</summary>
    public sealed class LinkUnit
    {
        public LinkUnit(string? name, int devices, int loads)
        {
            Name = name;
            Devices = devices;
            Loads = loads;
        }

        /// <summary>What it is, for the packed link's contents list — a panel name, or an interface.</summary>
        public string? Name { get; }

        public int Devices { get; }
        public int Loads { get; }
    }

    /// <summary>How the demand landed. When packed against a fixed link budget, <see cref="Links"/>
    /// has exactly that many entries, QS first and Clear Connect last.</summary>
    public sealed class LinkPackResult
    {
        public LinkPackResult(IReadOnlyList<PackedLink> links, int qsLinkCount, int clearConnectLinkCount)
        {
            Links = links;
            QsLinkCount = qsLinkCount;
            ClearConnectLinkCount = clearConnectLinkCount;
        }

        public IReadOnlyList<PackedLink> Links { get; }
        public int QsLinkCount { get; }
        public int ClearConnectLinkCount { get; }
        public int TotalLinkCount => QsLinkCount + ClearConnectLinkCount;
    }

    /// <summary>One link's contents after packing.</summary>
    public sealed class PackedLink
    {
        public PackedLink(string linkType, int devices, int loads, IReadOnlyList<string> unitNames,
            int repeaters = 0)
        {
            LinkType = linkType;
            Devices = devices;
            Loads = loads;
            UnitNames = unitNames;
            Repeaters = repeaters;
        }

        public string LinkType { get; }

        /// <summary>Everything on the link, repeaters included — they are devices like anything else.</summary>
        public int Devices { get; }

        public int Loads { get; }

        /// <summary>How many of <see cref="Devices"/> are Hybrid Repeaters, which carry their own much
        /// lower cap. A subset, not a second population.</summary>
        public int Repeaters { get; }

        /// <summary>Which units landed here. Nothing renders this yet — it is what makes a packing
        /// decision explicable when one needs explaining.</summary>
        public IReadOnlyList<string> UnitNames { get; }

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
