using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// The full job contract (§1.5) as flat declared values — every knob the engine reads. Build one
    /// by hand (decoder POOL, driver pool, voltage, ceiling, reserved, D4) and the bill is a pure
    /// function of it plus the tagged zones. No part is named in code.
    /// </summary>
    public sealed class DmxContract
    {
        public DmxContract(IReadOnlyList<DecoderSpec> decoderPool, IReadOnlyList<DriverType> driverPool,
                           double systemVolts, int channelCeiling, int reservedChannels, int maxDevicesPerSegment,
                           double breakerAmps = 20.0, double feedVolts = 120.0,
                           double breakerContinuousDerate = 0.8, int maxDriversPerBreaker = 0,
                           BreakerBasis breakerBasis = BreakerBasis.ConnectedLoad,
                           int linkChannelCapacity = 512, int linkDeviceCapacity = 99, int linksPerProcessor = 2)
        {
            DecoderPool = decoderPool;
            DriverPool = driverPool;
            SystemVolts = systemVolts;
            ChannelCeiling = channelCeiling;
            ReservedChannels = reservedChannels;
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
        public int ChannelCeiling { get; }       // §1.6 profile: Lutron 32 / native 512
        public int ReservedChannels { get; }     // §3c smart-fixture reservation
        public int MaxDevicesPerSegment { get; } // D4 (~32 default), an input

        // §0c 120 V feed pass: breaker = amps × volts × continuous-derate, plus an inrush count cap.
        public double BreakerAmps { get; }              // 20 A typical
        public double FeedVolts { get; }                // 120 V line side
        public double BreakerContinuousDerate { get; }  // NEC 80% ⇒ 0.8 (DeratingFactor.Normalize rules)
        public int MaxDriversPerBreaker { get; }        // inrush cap; 0 = no count limit
        public BreakerBasis BreakerBasis { get; }       // pack by connected load or full nameplate

        // §8b / Q8 Link→Processor roll-up (report-only, §1.6 profile values). Lutron QS / HQP7-2 defaults.
        public int LinkChannelCapacity { get; }  // switch legs per link (1 DMX ch = 1 leg); QS = 512
        public int LinkDeviceCapacity { get; }   // devices (interfaces) per link; QS = 99
        public int LinksPerProcessor { get; }    // links per processor; HQP7-2 = 2

        /// <summary>The branch-breaker watt cap drivers are load-packed under (§0c).</summary>
        public double BreakerCapWatts => BreakerPacker.Cap(BreakerAmps, FeedVolts, BreakerContinuousDerate);
    }

    /// <summary>
    /// A PHYSICAL cluster of runs (§8d): the runs close enough to share decoders — one wall, one cove,
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
    /// One control zone as the designer tagged it (§5): a name + its physical clusters. A zone is the
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
    /// A designer-declared DMX Loop (Design §0d / §6c): a named, ordered grouping of Control Zones that
    /// must share ONE interface/chain (= one one-line diagram). Zones are referenced by name (the harness
    /// analog of the Switch-ID the real module keys on). Declaring a loop forces those zones onto their
    /// own interface; any zone in NO declared loop falls through to engine auto-packing (the geometry-blind
    /// next-fit). A loop summing more channels than one interface ceiling carries is the **third pre-solve
    /// hard-stop** (<see cref="OverCapLoopsException"/>) — the cable break is the designer's geometry call,
    /// so the engine refuses rather than silently splitting the chain.
    /// </summary>
    public sealed class LoopDeclaration
    {
        public LoopDeclaration(string name, IReadOnlyList<string> zoneNames)
        {
            Name = name;
            ZoneNames = zoneNames;
        }

        public string Name { get; }

        /// <summary>The Control Zones (by name) this loop groups onto one interface, in chain order.</summary>
        public IReadOnlyList<string> ZoneNames { get; }
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

        /// <summary>This interface's 120 V feeds (§0c) — one <see cref="BreakerLoad"/> per feed, drivers in
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

        /// <summary>The 120 V branch breakers drivers were load-packed onto (§0c) — one feed per breaker.</summary>
        public IReadOnlyList<BreakerLoad> Breakers { get; }

        /// <summary>The control Links the interfaces roll up onto (§8b/Q8) — report-only DMX demand.</summary>
        public IReadOnlyList<LinkLoad> Links { get; }

        /// <summary>Links per processor (§1.6 profile; HQP7-2 = 2) used for the processor roll-up.</summary>
        public int LinksPerProcessor { get; }

        public int TotalDecoders => Zones.Sum(z => z.DecoderCount);
        public int TotalDrivers => Zones.Sum(z => z.DriverCount);
        public int RequiredBreakers => Breakers.Count;
        public int InterfaceCount => Interfaces.Count;

        /// <summary>Control links the interfaces imply (§8b/Q8) — reported, never provisioned.</summary>
        public int RequiredLinks => Links.Count;

        /// <summary>Processors the links imply: ceil(links / LinksPerProcessor) (§8b/Q8).</summary>
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
    /// pre-solve gates (<see cref="DmxValidator"/>): the §6c contract hard-stop
    /// (<see cref="UnmappableTapeException"/>), the drawn-correctly over-cap gate
    /// (<see cref="OverCapRunsException"/>), and the declared-loop-over-ceiling gate
    /// (<see cref="OverCapLoopsException"/>) — never silently splits a drawn run or a declared loop.
    /// </summary>
    public static class DmxSolver
    {
        /// <param name="loops">
        /// Optional designer-declared DMX Loops (§0d): each forces its zones onto one interface. Zones in
        /// no declared loop fall through to engine auto-packing. Null/empty ⇒ pure auto-packing (the
        /// original behavior).
        /// </param>
        public static DmxBill Solve(DmxContract contract, IReadOnlyList<ZoneDesign> zones,
                                    IReadOnlyList<LoopDeclaration>? loops = null)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            // 0. Gate: mappability (§6c) + drawn-correctly over-cap + declared-loop over-ceiling (§0d).
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
            //    each become one interface (in declaration order); the rest auto-pack (§0d).
            var packed = InterfacePacker.Pack(zoneInputs, contract.ChannelCeiling, contract.ReservedChannels, loops);

            // 3. Per interface: split the loop by D4 (segments) AND pack its drivers onto 120 V feeds (§0c).
            //    Feeds pack PER INTERFACE in DEC-walk order (next-fit) so a feed is consecutive DEC#s and
            //    never spans interfaces — the §0c count then equals the one-line's drawn "120V FEED" blocks
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
                var feeds = BreakerPacker.Pack(ifaceDriverWatts, contract.BreakerCapWatts, contract.MaxDriversPerBreaker);

                interfaceSolutions.Add(new InterfaceSolution(iface, deviceCount, segmentation, feeds));
            }

            // 4. Feeds roll up to the bill: bill.Breakers = the per-interface feeds, flattened in order.
            var breakers = interfaceSolutions.SelectMany(i => i.Feeds).ToList();

            // 5. Roll-up (§8b/Q8, report-only): pack interfaces onto control links (legs + device caps),
            //    then links → processors. Sized & reported; never a solve stop, never provisioned.
            var links = LinkPacker.Pack(interfaceSolutions.Select(i => i.Interface.ChannelsUsed).ToList(),
                                        contract.LinkChannelCapacity, contract.LinkDeviceCapacity);

            return new DmxBill(zoneSolutions, interfaceSolutions, breakers, links, contract.LinksPerProcessor);
        }
    }
}
