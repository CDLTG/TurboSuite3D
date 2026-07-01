using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Designer-declared DMX Loops (Design §0d) — the Zone→Loop declaration. A declared loop forces its
    /// zones onto one interface/chain (= one one-line diagram); zones in no declared loop fall through to
    /// engine auto-packing. A declared loop is a PHYSICAL chain capped at the interface ceiling, so
    /// over-ceiling assignment is the THIRD pre-solve hard-stop (batched <see cref="OverCapLoopsException"/>),
    /// never a silent split. D3/D4 overflow on a declared loop is NOT a stop (repeater → segments within).
    /// </summary>
    public class LoopDeclarationTests
    {
        private const double V = 24.0;

        private static DmxContract Contract(int ceiling = 32, int d4 = 32) => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
            driverPool: new[] { new DriverType("MD", 480, V, 0.85), new DriverType("ME", 600, V, 0.85) },
            systemVolts: V, channelCeiling: ceiling, maxDevicesPerSegment: d4);

        // A tiny single-decoder zone of the given channel count (wattsPerFt = 1 ⇒ length is watts).
        private static ZoneDesign Zone(string name, int channels, double watts = 5.0)
            => new ZoneDesign(name, new[] { new TapeRun(watts, 1.0, channels) });

        private static ZoneDesign[] Zones(int count, int channels, string prefix = "Z")
            => Enumerable.Range(1, count).Select(i => Zone($"{prefix}{i}", channels)).ToArray();

        private static LoopDeclaration Loop(string name, params string[] zoneNames)
            => new LoopDeclaration(name, zoneNames);

        // --- Force-separate: the engine's whole reason loops are declarable (§0d) ----------------------

        [Fact]
        public void NoLoops_AutoPacks_TwoSmallZones_OntoOneInterface()
        {
            // 4 + 4 = 8 ch ≤ 32 ⇒ the geometry-blind next-fit puts both on one interface.
            var bill = DmxSolver.Solve(Contract(), new[] { Zone("Z1", 4), Zone("Z2", 4) });

            Assert.Equal(1, bill.InterfaceCount);
            Assert.Null(bill.Interfaces.Single().Interface.LoopName); // auto-packed, unbranded
        }

        [Fact]
        public void DeclaringTwoLoops_ForcesSeparateInterfaces_EvenThoughTheyFitTogether()
        {
            // Same two zones, but declared as separate loops ⇒ two interfaces (force-separate), each branded.
            var zones = new[] { Zone("Z1", 4), Zone("Z2", 4) };
            var loops = new[] { Loop("East", "Z1"), Loop("West", "Z2") };

            var bill = DmxSolver.Solve(Contract(), zones, loops);

            Assert.Equal(2, bill.InterfaceCount);
            Assert.Equal(new[] { "East", "West" }, bill.Interfaces.Select(i => i.Interface.LoopName).ToArray());
        }

        // --- Declared loop carries exactly its zones; the rest auto-pack alongside (§0d) ---------------

        [Fact]
        public void DeclaredLoop_GroupsItsZones_AndUnassignedZonesAutoPack()
        {
            // East = {Z1, Z2} (declared, one chain); Z3 left undeclared ⇒ its own auto-packed interface.
            var zones = new[] { Zone("Z1", 4), Zone("Z2", 4), Zone("Z3", 4) };
            var loops = new[] { Loop("East", "Z1", "Z2") };

            var bill = DmxSolver.Solve(Contract(), zones, loops);

            Assert.Equal(2, bill.InterfaceCount);

            var east = bill.Interfaces[0].Interface;           // declared loops come first, in order
            Assert.Equal("East", east.LoopName);
            Assert.Equal(new[] { "Z1", "Z2" }, east.Zones.Select(z => z.ZoneName).ToArray());

            var auto = bill.Interfaces[1].Interface;
            Assert.Null(auto.LoopName);
            Assert.Equal(new[] { "Z3" }, auto.Zones.Select(z => z.ZoneName).ToArray());
        }

        [Fact]
        public void DeclaredLoop_ExactlyAtCeiling_IsAllowed()
        {
            // 8 zones × 4 ch = 32 = the ceiling — the boundary fits one chain, no stop.
            var zones = Zones(8, 4);
            var loops = new[] { Loop("Full", zones.Select(z => z.ZoneName).ToArray()) };

            var bill = DmxSolver.Solve(Contract(32), zones, loops);

            Assert.Equal(1, bill.InterfaceCount);
            Assert.Equal(32, bill.Interfaces.Single().Interface.ChannelsUsed);
        }

        // --- The third gate: a declared loop over the interface ceiling (§0d) --------------------------

        [Fact]
        public void DeclaredLoop_OverCeiling_IsTheThirdHardStop()
        {
            // 9 zones × 4 ch = 36 > 32 ⇒ unbuildable as one chain. Refuse the whole solve (no partial bill).
            var zones = Zones(9, 4);
            var loops = new[] { Loop("TooBig", zones.Select(z => z.ZoneName).ToArray()) };

            var ex = Assert.Throws<OverCapLoopsException>(() => DmxSolver.Solve(Contract(32), zones, loops));

            var v = Assert.Single(ex.Violations);
            Assert.Equal("TooBig", v.LoopName);
            Assert.Equal(36, v.Channels);
            Assert.Equal(32, v.Budget);
            Assert.Equal(2, v.MinLoops); // ceil(36 / 32)
        }

        [Fact]
        public void OverCeilingLoops_AreAllReportedAtOnce_Batched()
        {
            var a = Zones(9, 4, "A"); // 36 ch — over
            var b = Zones(9, 4, "B"); // 36 ch — over
            var zones = a.Concat(b).ToArray();
            var loops = new[]
            {
                Loop("LoopA", a.Select(z => z.ZoneName).ToArray()),
                Loop("LoopB", b.Select(z => z.ZoneName).ToArray()),
            };

            var ex = Assert.Throws<OverCapLoopsException>(() => DmxSolver.Solve(Contract(32), zones, loops));

            Assert.Equal(new[] { "LoopA", "LoopB" }, ex.Violations.Select(v => v.LoopName).ToArray());
        }

        [Fact]
        public void LoopReserved_TightensThatLoopsCeiling()
        {
            // 8 × 4 = 32 fits a 32 ceiling, but the loop reserving 4 leaves a 28 budget ⇒ same loop now over.
            var zones = Zones(8, 4);
            var loops = new[] { new LoopDeclaration("Full", zones.Select(z => z.ZoneName).ToList(), reservedChannels: 4) };
            var contract = new DmxContract(
                decoderPool: new[] { DecoderSpec.Dmx4_5000_10A },
                driverPool: new[] { new DriverType("ME", 600, V, 0.85) },
                systemVolts: V, channelCeiling: 32, maxDevicesPerSegment: 32);

            var ex = Assert.Throws<OverCapLoopsException>(() => DmxSolver.Solve(contract, zones, loops));
            var v = Assert.Single(ex.Violations);
            Assert.Equal(32, v.Channels);
            Assert.Equal(28, v.Budget);
        }

        // --- Declaration integrity (malformed input, immediate throw) ----------------------------------

        [Fact]
        public void Loop_ReferencingUnknownZone_Throws()
        {
            var zones = new[] { Zone("Z1", 4) };
            var loops = new[] { Loop("Bad", "Ghost") };

            var ex = Assert.Throws<LoopDeclarationException>(() => DmxSolver.Solve(Contract(), zones, loops));
            Assert.Contains("Ghost", ex.Message);
        }

        [Fact]
        public void ZoneInTwoLoops_Throws()
        {
            var zones = new[] { Zone("Z1", 4), Zone("Z2", 4) };
            var loops = new[] { Loop("L1", "Z1"), Loop("L2", "Z1") }; // Z1 on two chains — impossible

            var ex = Assert.Throws<LoopDeclarationException>(() => DmxSolver.Solve(Contract(), zones, loops));
            Assert.Contains("Z1", ex.Message);
        }

        // --- D3/D4 on a declared loop is NOT a stop — repeaters split it WITHIN the one loop (§6b) ------

        [Fact]
        public void DeclaredLoop_OverDeviceCount_Repeats_DoesNotStop_DiagramIntact()
        {
            // The §8d cluster oracle as one declared loop: 72/60/72 TW sheets ⇒ 9 decoders, 2 channels.
            // With D4 = 4, the loop needs repeaters (ceil(9/4) = 3 segments ⇒ 2 repeaters) but stays ONE
            // interface/diagram — D3/D4 never break the chain, only D1 does.
            TapeRun[] Sheets(int n) => Enumerable.Range(0, n).Select(_ => new TapeRun(17.2, 1.0, 2)).ToArray();
            var zone = new ZoneDesign("Walls", new[]
            {
                new RunCluster("East",  Sheets(72)),
                new RunCluster("North", Sheets(60)),
                new RunCluster("West",  Sheets(72)),
            });
            var loops = new[] { Loop("WallsLoop", "Walls") };

            var bill = DmxSolver.Solve(Contract(ceiling: 32, d4: 4), new[] { zone }, loops);

            Assert.Equal(1, bill.InterfaceCount);                              // one chain, one diagram
            Assert.Equal("WallsLoop", bill.Interfaces.Single().Interface.LoopName);
            Assert.Equal(9, bill.TotalDecoders);
            Assert.Equal(2, bill.TotalChannels);
            Assert.Equal(2, bill.TotalRepeaters);                             // ceil(9/4) - 1
        }

        // --- Parser round-trip -------------------------------------------------------------------------

        [Fact]
        public void Parse_ReadsLoopLine_AndSolves()
        {
            const string text = @"
wattsPerFt = 1
ceiling = 32
decoder = 4ch outputs:4 amps:10 watts:960
driver = MD 480 24 0.85
zone = Z1 | 4 | 5
zone = Z2 | 4 | 5
loop = East | Z1, Z2
";
            var s = ScenarioParser.Parse(text);

            var loop = Assert.Single(s.Loops);
            Assert.Equal("East", loop.Name);
            Assert.Equal(new[] { "Z1", "Z2" }, loop.ZoneNames.ToArray());

            var bill = DmxSolver.Solve(s.Contract, s.Zones, s.Loops);
            Assert.Equal(1, bill.InterfaceCount);
            Assert.Equal("East", bill.Interfaces.Single().Interface.LoopName);
        }
    }
}
