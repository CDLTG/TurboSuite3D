using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// The full job contract as flat declared values — every knob the engine reads. Build one
    /// by hand (decoder POOL, driver pool, voltage, ceiling, D4) and the bill is a pure function of it
    /// plus the tagged zones. No part is named in code. Smart-fixture channel reservation is a
    /// per-loop property (<see cref="LoopDeclaration.ReservedChannels"/>) — auto-packed interfaces
    /// reserve nothing.
    /// </summary>
    public sealed class DmxContract
    {
        public DmxContract(IReadOnlyList<DecoderSpec> decoderPool, IReadOnlyList<DriverType> driverPool,
                           double systemVolts, int channelCeiling, int maxDevicesPerSegment,
                           double breakerAmps = 20.0, double feedVolts = 120.0,
                           double breakerContinuousDerate = 0.8, int maxDriversPerBreaker = 0,
                           BreakerBasis breakerBasis = BreakerBasis.ConnectedLoad,
                           int linkChannelCapacity = 512, int linkDeviceCapacity = 99, int linksPerProcessor = 2)
        {
            DecoderPool = decoderPool;
            DriverPool = driverPool;
            SystemVolts = systemVolts;
            ChannelCeiling = channelCeiling;
            MaxDevicesPerSegment = maxDevicesPerSegment;
            BreakerAmps = breakerAmps;
            FeedVolts = feedVolts;
            BreakerContinuousDerate = breakerContinuousDerate;
            MaxDriversPerBreaker = maxDriversPerBreaker;
            BreakerBasis = breakerBasis;
            LinkChannelCapacity = linkChannelCapacity;
            LinkDeviceCapacity = linkDeviceCapacity;
            LinksPerProcessor = linksPerProcessor;
        }

        public IReadOnlyList<DecoderSpec> DecoderPool { get; }
        public IReadOnlyList<DriverType> DriverPool { get; }
        public double SystemVolts { get; }
        public int ChannelCeiling { get; } // profile: Lutron 32 / native 512
        public int MaxDevicesPerSegment { get; } // D4 (~32 default), an input

        // 120 V feed pass: breaker = amps × volts × continuous-derate, plus an inrush count cap.
        public double BreakerAmps { get; }              // 20 A typical
        public double FeedVolts { get; }                // 120 V line side
        public double BreakerContinuousDerate { get; }  // NEC 80% ⇒ 0.8 (DeratingFactor.Normalize rules)
        public int MaxDriversPerBreaker { get; }        // inrush cap; 0 = no count limit
        public BreakerBasis BreakerBasis { get; }       // pack by connected load or full nameplate

        // Link→Processor roll-up (report-only, profile values). Lutron QS / HQP7-2 defaults.
        public int LinkChannelCapacity { get; }  // switch legs per link (1 DMX ch = 1 leg); QS = 512
        public int LinkDeviceCapacity { get; }   // devices (interfaces) per link; QS = 99
        public int LinksPerProcessor { get; }    // links per processor; HQP7-2 = 2

        /// <summary>The branch-breaker watt cap drivers are load-packed under.</summary>
        public double BreakerCapWatts => BreakerPacker.Cap(BreakerAmps, FeedVolts, BreakerContinuousDerate);
    }

    /// <summary>
    /// A PHYSICAL cluster of runs: the runs close enough to share decoders — one wall, one cove,
    /// one location. Decoders pack PER cluster (a decoder can't reach across the room), so a cluster is
    /// the decoder-packing grain. It's orthogonal to the control zone (the addressing grain): one zone
    /// holds one or more clusters, all mirrored to the zone's single address.
    /// </summary>
    public sealed class RunCluster
    {
        public RunCluster(string name, IReadOnlyList<TapeRun> runs)
        {
            Name = name;
            Runs = runs;
        }

        public string Name { get; }
        public IReadOnlyList<TapeRun> Runs { get; }
    }

    /// <summary>
    /// One control zone as the designer tagged it: a name + its physical clusters. A zone is the
    /// ADDRESSING grain (one mirrored DMX address); clusters are the DECODER-PACKING grain within it.
    /// </summary>
    public sealed class ZoneDesign
    {
        public ZoneDesign(string zoneName, IReadOnlyList<RunCluster> clusters)
        {
            ZoneName = zoneName;
            Clusters = clusters;
        }

        /// <summary>Convenience: a flat run list = one cluster (the pre-cluster, single-location case).</summary>
        public ZoneDesign(string zoneName, IReadOnlyList<TapeRun> runs)
            : this(zoneName, new[] { new RunCluster(zoneName, runs) }) { }

        public string ZoneName { get; }
        public IReadOnlyList<RunCluster> Clusters { get; }

        /// <summary>All runs across every cluster — for zone-level checks (channel count, over-cap gate).</summary>
        public IReadOnlyList<TapeRun> Runs => Clusters.SelectMany(c => c.Runs).ToList();
    }

    /// <summary>
    /// A designer-declared DMX Loop: a named, ordered grouping of Control Zones that
    /// must share ONE interface/chain (= one one-line diagram). Zones are referenced by name (the harness
    /// analog of the Switch-ID the real module keys on). Declaring a loop forces those zones onto their
    /// own interface; any zone in NO declared loop falls through to engine auto-packing (the geometry-blind
    /// next-fit). A loop summing more channels than one interface ceiling carries is the **third pre-solve
    /// hard-stop** (<see cref="OverCapLoopsException"/>) — the cable break is the designer's geometry call,
    /// so the engine refuses rather than silently splitting the chain.
    /// </summary>
    public sealed class LoopDeclaration
    {
        public LoopDeclaration(string name, IReadOnlyList<string> zoneNames, int reservedChannels = 0)
        {
            Name = name;
            ZoneNames = zoneNames;
            ReservedChannels = reservedChannels < 0 ? 0 : reservedChannels;
        }

        public string Name { get; }

        /// <summary>The Control Zones (by name) this loop groups onto one interface, in chain order.</summary>
        public IReadOnlyList<string> ZoneNames { get; }

        /// <summary>Channels reserved off this loop's interface budget for smart fixtures the tape packer
        /// doesn't place. 0 = the whole ceiling is available to tape. Per-loop because the fixtures
        /// that motivate a reservation live in a specific loop; auto-packed interfaces reserve nothing.</summary>
        public int ReservedChannels { get; }
    }

    /// <summary>One physical cluster's power pack inside a zone solution: its name and its powered decoders.</summary>
    public sealed class ClusterSolution
    {
        public ClusterSolution(string name, PowerPackResult power)
        {
            Name = name;
            Power = power;
        }

        public string Name { get; }
        public PowerPackResult Power { get; }
        public int DecoderCount => Power.DecoderCount;
    }

    /// <summary>
    /// Per-zone result inside the bill: its channel count, the decoder type chosen, and the per-cluster
    /// power packs. Decoders are summed across clusters; they all share the zone's one mirrored address.
    /// </summary>
    public sealed class ZoneSolution
    {
        public ZoneSolution(string zoneName, int channels, DecoderSpec decoder, IReadOnlyList<ClusterSolution> clusters)
        {
            ZoneName = zoneName;
            Channels = channels;
            Decoder = decoder;
            Clusters = clusters;
        }

        public string ZoneName { get; }
        public int Channels { get; }
        public DecoderSpec Decoder { get; }
        public IReadOnlyList<ClusterSolution> Clusters { get; }

        /// <summary>Every powered decoder across all clusters — all mirrored to the zone's one address.</summary>
        public IReadOnlyList<PoweredDecoder> Decoders => Clusters.SelectMany(c => c.Power.Decoders).ToList();
        public int DecoderCount => Clusters.Sum(c => c.DecoderCount);
        public int DriverCount => DecoderCount;
    }

    /// <summary>Per-interface (loop) result inside the bill: addressed zones + its segmentation + 120 V feeds.</summary>
    public sealed class InterfaceSolution
    {
        public InterfaceSolution(DmxInterface dmxInterface, int deviceCount, LoopSegmentation segmentation,
                                 IReadOnlyList<BreakerLoad> feeds)
        {
            Interface = dmxInterface;
            DeviceCount = deviceCount;
            Segmentation = segmentation;
            Feeds = feeds;
        }

        public DmxInterface Interface { get; }
        public int DeviceCount { get; }
        public LoopSegmentation Segmentation { get; }
        public int RepeaterCount => Segmentation.RepeaterCount;

        /// <summary>This interface's 120 V feeds — one <see cref="BreakerLoad"/> per feed, drivers in
        /// DEC order (next-fit, never spanning interfaces). The one-line draws these as the "120V FEED"
        /// blocks; <c>bill.Breakers</c> is these flattened across interfaces, so the count and the drawing
        /// agree by construction.</summary>
        public IReadOnlyList<BreakerLoad> Feeds { get; }
    }

    /// <summary>The complete deterministic bill for one Control System solve.</summary>
    public sealed class DmxBill
    {
        public DmxBill(IReadOnlyList<ZoneSolution> zones, IReadOnlyList<InterfaceSolution> interfaces,
                       IReadOnlyList<BreakerLoad> breakers, IReadOnlyList<LinkLoad> links, int linksPerProcessor)
        {
            Zones = zones;
            Interfaces = interfaces;
            Breakers = breakers;
            Links = links;
            LinksPerProcessor = linksPerProcessor;
        }

        public IReadOnlyList<ZoneSolution> Zones { get; }
        public IReadOnlyList<InterfaceSolution> Interfaces { get; }

        /// <summary>The 120 V branch breakers drivers were load-packed onto — one feed per breaker.</summary>
        public IReadOnlyList<BreakerLoad> Breakers { get; }

        /// <summary>The control Links the interfaces roll up onto — report-only DMX demand.</summary>
        public IReadOnlyList<LinkLoad> Links { get; }

        /// <summary>Links per processor ( profile; HQP7-2 = 2) used for the processor roll-up.</summary>
        public int LinksPerProcessor { get; }

        public int TotalDecoders => Zones.Sum(z => z.DecoderCount);
        public int TotalDrivers => Zones.Sum(z => z.DriverCount);
        public int RequiredBreakers => Breakers.Count;
        public int InterfaceCount => Interfaces.Count;

        /// <summary>Control links the interfaces imply — reported, never provisioned.</summary>
        public int RequiredLinks => Links.Count;

        /// <summary>Processors the links imply: ceil(links / LinksPerProcessor).</summary>
        public int RequiredProcessors => LinkPacker.ProcessorCount(Links.Count, LinksPerProcessor);
        public int TotalChannels => Interfaces.Sum(i => i.Interface.ChannelsUsed);
        public int TotalRepeaters => Interfaces.Sum(i => i.RepeaterCount);
        public double TotalWatts => Zones.Sum(z => z.Decoders.Sum(d => d.Decoder.TotalWatts));

        /// <summary>Decoder count by type name (the decoder BOM — e.g. 4ch × 12, 6ch × 2).</summary>
        public IReadOnlyDictionary<string, int> DecodersByType =>
            Zones.GroupBy(z => z.Decoder.Name)
                 .ToDictionary(g => g.Key, g => g.Sum(z => z.DecoderCount));

        /// <summary>Driver count by Type name (the driver BOM).</summary>
        public IReadOnlyDictionary<string, int> DriversByType =>
            Zones.SelectMany(z => z.Decoders)
                 .GroupBy(d => d.Driver.Name)
                 .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// The whole pure pipeline in one call: select a decoder type + power-pack each zone → address &amp;
    /// interface-pack all zones → segment each loop by D4. Deterministic; refuses only at the three
    /// pre-solve gates (<see cref="DmxValidator"/>): the contract hard-stop
    /// (<see cref="UnmappableTapeException"/>), the drawn-correctly over-cap gate
    /// (<see cref="OverCapRunsException"/>), and the declared-loop-over-ceiling gate
    /// (<see cref="OverCapLoopsException"/>) — never silently splits a drawn run or a declared loop.
    /// </summary>
    /// <remarks>
    /// CANONICAL DMX VOCABULARY — the containment ladder (largest → smallest). Each rung physically
    /// contains the one below it; capacity is tiered &amp; growable (fill a rung ⇒ add another of it). The
    /// solve spine is: Project → Processor → Link → Interface → DMX Loop → Decoder → DMX Fixture. Other
    /// files cite these rung numbers; this is their definition.
    /// <list type="number">
    /// <item><b>Project</b> — the connected control-system file (one job). Grows freely (add Processors).</item>
    /// <item><b>Processor</b> — the control head-end unit (Lutron HQP7-2 = 2 Links). Report-only.</item>
    /// <item><b>Link</b> — a control trunk off the Processor that the Interfaces hang on. Lutron QS =
    ///   <b>512 switch legs / 99 devices</b> (1 DMX channel = 1 switch leg), <i>shared with all non-DMX
    ///   Lutron loads</i> ⇒ TurboDMX sizes &amp; REPORTS it, never enforces (D2). See <see cref="LinkPacker"/>.</item>
    /// <item><b>Interface</b> — a DMX gateway emitting one universe; the universe divides into
    ///   <i>channels</i> (the budget atom). <b>1 Interface : 1 DMX Loop.</b> Channel ceiling is a profile
    ///   value (Lutron 32 / native 512). See <see cref="DmxProfile"/>, <see cref="InterfacePacker"/>.</item>
    /// <item><b>DMX Loop</b> — the daisy chain of Decoders off one Interface (+ terminator) = <b>one
    ///   one-line diagram</b>. Designer-declarable (Zone→Loop, <see cref="LoopDeclaration"/>); an
    ///   undeclared zone auto-packs. Split into signal segments by repeaters.
    ///   <list type="bullet"><item><b>5a. Signal segment</b> — one repeater-bounded RS-485 run:
    ///   ≤ ~32 devices (D4) / ≤ 1000 ft (D3), vendor-independent physics. See <see cref="LoopSegmenter"/>.</item></list></item>
    /// <item><b>Decoder</b> — the DMX <i>device</i> on the Loop; turns channels into LED power outputs.
    ///   Selected from the pool by output count (smallest whose outputs ≥ the tape's channels). Set to a
    ///   Control Zone's DMX address. Counts toward a segment's ~32.</item>
    /// <item><b>DMX Fixture</b> — the controlled LED load; declares an integer <c>DMX Channels</c>
    ///   (<c>&gt; 0</c> marks ANY DMX fixture, linear or point). A Revit lighting fixture carrying
    ///   <c>Control Zone</c>.</item>
    /// </list>
    /// LOGICAL GROUPINGS (cut across the ladder, not rungs):
    /// <list type="bullet">
    /// <item><b>Control Zone</b> — design intent: the tapes (and their decoders) sharing ONE DMX address,
    ///   mirrored across all the zone's decoders. The one true human input (a native fixture param). Can
    ///   span Loops by duplicating its address.</item>
    /// <item><b>Physical cluster</b> — geometry: runs close enough to share a Decoder (one wall/cove). The
    ///   decoder-packing grain, orthogonal to the Control Zone (the addressing grain). See <see cref="DmxZoneBuilder"/>.</item>
    /// <item><b>DMX address</b> — the coordinate a Control Zone owns (e.g. "005"): the start channel its
    ///   decoders are set to. Not a rung — it's what a Control Zone <i>is</i>, in the Interface's channels.</item>
    /// </list>
    /// OVERLOADED WORDS (disambiguate on sight): <b>Device</b> = an Interface (on the Link) OR a Decoder
    /// (on the Loop) — always say which. <b>Loop</b> = one Interface's full chain; <b>segment</b> = its
    /// repeater-bounded ~32-device piece. <b>Channel</b> = one DMX slot / budget atom (= 1 switch leg on
    /// Lutron), a count; <b>address</b> = a Control Zone's start channel, a position. <b>Zone</b> always
    /// means Control Zone.
    /// </remarks>
    public static class DmxSolver
    {
        /// <param name="loops">
        /// Optional designer-declared DMX Loops: each forces its zones onto one interface. Zones in
        /// no declared loop fall through to engine auto-packing. Null/empty ⇒ pure auto-packing (the
        /// original behavior).
        /// </param>
        public static DmxBill Solve(DmxContract contract, IReadOnlyList<ZoneDesign> zones,
                                    IReadOnlyList<LoopDeclaration>? loops = null)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            // 0. Gate: mappability + drawn-correctly over-cap + declared-loop over-ceiling.
            //    Refuses before any partial bill.
            DmxValidator.Validate(contract, zones, loops);

            // 1. Power: select a decoder type per zone (blocker if none fits), then pack.
            var zoneSolutions = new List<ZoneSolution>(zones.Count);
            var decoderCountByZone = new Dictionary<string, int>();
            var zoneInputs = new List<ZoneInput>(zones.Count);
            foreach (var zone in zones)
            {
                int channels = DecoderPacker.SingleChannelsOf(zone.Runs);
                var decoder = DecoderSelector.SelectForChannels(contract.DecoderPool, channels);
                if (decoder is null)
                    throw new UnmappableTapeException(zone.ZoneName, channels, DecoderSelector.MaxOutputs(contract.DecoderPool));

                // Pack decoders PER physical cluster (a decoder can't reach across the room), then sum.
                var clusterSolutions = new List<ClusterSolution>(zone.Clusters.Count);
                foreach (var cluster in zone.Clusters)
                {
                    var power = PowerPacker.Pack(cluster.Runs, decoder.Value, contract.SystemVolts, contract.DriverPool);
                    clusterSolutions.Add(new ClusterSolution(cluster.Name, power));
                }

                var zoneSolution = new ZoneSolution(zone.ZoneName, channels, decoder.Value, clusterSolutions);
                zoneSolutions.Add(zoneSolution);
                decoderCountByZone[zone.ZoneName] = zoneSolution.DecoderCount;
                zoneInputs.Add(new ZoneInput(zone.ZoneName, channels, zoneSolution.DecoderCount));
            }

            // 2. Control: address zones and pack them into interfaces under the D1 budget. Declared loops
            // each become one interface (in declaration order); the rest auto-pack.
            var packed = InterfacePacker.Pack(zoneInputs, contract.ChannelCeiling, loops);

            // 3. Per interface: split the loop by D4 (segments) AND pack its drivers onto 120 V feeds.
            //    Feeds pack PER INTERFACE in DEC-walk order (next-fit) so a feed is consecutive DEC#s and
            // never spans interfaces — the count then equals the one-line's drawn "120V FEED" blocks
            //    (gap closed). Per-driver watts are connected load OR full nameplate, per the contract basis.
            var byZone = zoneSolutions.ToDictionary(z => z.ZoneName);
            var interfaceSolutions = new List<InterfaceSolution>(packed.Interfaces.Count);
            foreach (var iface in packed.Interfaces)
            {
                int deviceCount = iface.Zones.Sum(z => decoderCountByZone[z.ZoneName]);
                var segmentation = LoopSegmenter.Segment(deviceCount, contract.MaxDevicesPerSegment);

                var ifaceDriverWatts = iface.Zones
                    .SelectMany(z => byZone[z.ZoneName].Decoders)     // DEC-walk order: zones → clusters → decoders
                    .Select(d => contract.BreakerBasis == BreakerBasis.DriverRating
                                     ? d.Driver.RatedWatts            // nameplate: worst-case / inrush-sized
                                     : d.Decoder.TotalWatts)          // connected load: actual draw
                    .ToList();
                // The manual per-breaker count cap is the ConnectedLoad ("more control") knob — the designer
                // owns inrush by hand there. Nameplate packing is already inrush-honest (surge scales with a
                // supply's rated capacity, which is what it charges), so the count cap doesn't apply.
                int effectiveMaxPerBreaker = contract.BreakerBasis == BreakerBasis.DriverRating
                                                 ? 0 : contract.MaxDriversPerBreaker;
                var feeds = BreakerPacker.Pack(ifaceDriverWatts, contract.BreakerCapWatts, effectiveMaxPerBreaker);

                interfaceSolutions.Add(new InterfaceSolution(iface, deviceCount, segmentation, feeds));
            }

            // 4. Feeds roll up to the bill: bill.Breakers = the per-interface feeds, flattened in order.
            var breakers = interfaceSolutions.SelectMany(i => i.Feeds).ToList();

            // 5. Roll-up (report-only): pack interfaces onto control links (legs + device caps),
            //    then links → processors. Sized & reported; never a solve stop, never provisioned.
            var links = LinkPacker.Pack(interfaceSolutions.Select(i => i.Interface.ChannelsUsed).ToList(),
                                        contract.LinkChannelCapacity, contract.LinkDeviceCapacity);

            return new DmxBill(zoneSolutions, interfaceSolutions, breakers, links, contract.LinksPerProcessor);
        }
    }
}
