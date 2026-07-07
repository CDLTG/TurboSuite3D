using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Lock;
using TurboSuite.Dmx.OneLine;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxOneLinePlanner"/> — the bill→one-line layout. Locks the
    /// per-loop symbol inventory, the DEC#/address/Type-Mark label params, the wire-type markers, the
    /// connection-point geometry (chain vertical, power horizontal), and — the headline — that the drawn
    /// "TO 20A BREAKER" feed blocks equal the breaker count (the reconciliation made visible in the drawing).
    /// </summary>
    public class DmxOneLinePlannerTests
    {
        private const double V = 24.0;
        private const double Eps = 1e-9;

        // Contract with an inrush count cap so one interface splits into several 120V FEED blocks.
        private static DmxContract Contract(int maxPerBreaker) => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
            driverPool: new[] { new DriverType("320", 320.0, V, 0.0),
                                new DriverType("480", 480.0, V, 0.0),
                                new DriverType("600", 600.0, V, 0.0) },
            systemVolts: V, channelCeiling: 512, maxDevicesPerSegment: 32,
            breakerAmps: 20, feedVolts: 120, breakerContinuousDerate: 0.8, maxDriversPerBreaker: maxPerBreaker);

        private static ZoneDesign Zone(string name, int decoders) =>
            new ZoneDesign(name, Enumerable.Range(0, decoders)
                .Select(_ => new TapeRun(94.3, 5.2, channels: 4)).ToArray());   // ~490 W ⇒ one decoder each

        private static DmxNumbering Fresh(DmxBill bill) =>
            DmxLockReconciler.Reconcile(DmxBillFlattener.Flatten(bill), baseline: null, locked: false);

        private static IReadOnlyList<DmxOneLineDrawing> Plan(DmxBill bill) =>
            DmxOneLinePlanner.Build(bill, Fresh(bill));

        private static int Count(DmxOneLineDrawing d, DmxSymbolKind k) => d.Symbols.Count(s => s.Kind == k);
        private static int Markers(DmxOneLineDrawing d, DmxWireType t) => d.Markers.Count(m => m.Type == t);
        private static int Feeds(DmxOneLineDrawing d) => d.Notes.Count(n => n.Text == "TO 20A\nBREAKER");

        [Fact]
        public void OneDrawingPerLoop_WithItsInterfaceNumber()
        {
            var bill = DmxSolver.Solve(Contract(0), new[] { Zone("Z1", 1), Zone("Z2", 1) },
                new[] { new LoopDeclaration("A", new[] { "Z1" }), new LoopDeclaration("B", new[] { "Z2" }) });
            var drawings = Plan(bill);

            Assert.Equal(2, drawings.Count);
            Assert.Equal(new[] { 1, 2 }, drawings.Select(d => d.InterfaceNumber).ToArray());
            Assert.Equal("A", drawings[0].LoopName);
            Assert.Equal("TurboDMX — Sys — Interface #1", drawings[0].ViewName("Sys"));
        }

        [Fact]
        public void EachLoopHasOneInterfaceProcessorTerminator_AndOneDriverDecoderPerDevice()
        {
            var bill = DmxSolver.Solve(Contract(0), new[] { Zone("Z1", 4) });
            var d = Assert.Single(Plan(bill));

            Assert.Equal(1, Count(d, DmxSymbolKind.Interface));
            Assert.Equal(1, Count(d, DmxSymbolKind.Processor));
            Assert.Equal(1, Count(d, DmxSymbolKind.Terminator));
            Assert.Equal(4, Count(d, DmxSymbolKind.Decoder));
            Assert.Equal(4, Count(d, DmxSymbolKind.Driver));
        }

        [Fact]
        public void DecoderBoxesCarryDecNumberAndZeroPaddedAddress_DriverCarriesTypeMark()
        {
            var bill = DmxSolver.Solve(Contract(0), new[] { Zone("Z1", 2) });
            var d = Assert.Single(Plan(bill));

            var decs = d.Symbols.Where(s => s.Kind == DmxSymbolKind.Decoder).ToList();
            Assert.Equal(new[] { "DEC 1", "DEC 2" },
                         decs.Select(s => s.Params[DmxOneLineGeometry.Decoder.DecNumberParam]).ToArray());
            // Address written zero-padded to 3 digits (label supplies the brackets).
            Assert.All(decs, s => Assert.Matches(@"^\d{3}$", s.Params[DmxOneLineGeometry.Decoder.AddressParam]));

            Assert.All(d.Symbols.Where(s => s.Kind == DmxSymbolKind.Driver),
                       s => Assert.Contains(s.Params[DmxOneLineGeometry.Driver.TypeMarkParam], new[] { "320", "480", "600" }));
            // Interface number param set.
            var iface = d.Symbols.Single(s => s.Kind == DmxSymbolKind.Interface);
            Assert.Equal("1", iface.Params[DmxOneLineGeometry.Interface.NumberParam]);
        }

        [Fact]
        public void DrawnFeedBlocksEqualTheSec0cBreakerCount()
        {
            // 5 decoders, inrush cap 2 ⇒ feeds [2,2,1] = 3 (count-bound). The drawing's "TO 20A BREAKER" notes
            // must equal the interface's feeds AND the bill's breaker count — the gap is closed.
            var bill = DmxSolver.Solve(Contract(2), new[] { Zone("Z1", 5) });
            var d = Assert.Single(Plan(bill));

            Assert.Equal(3, bill.RequiredBreakers);
            Assert.Equal(3, Feeds(d));
            Assert.Equal(bill.Interfaces.Single().Feeds.Count, Feeds(d));
        }

        [Fact]
        public void FeedBlocksSumToTheBreakerCountAcrossLoops()
        {
            var bill = DmxSolver.Solve(Contract(2), new[] { Zone("Z1", 3), Zone("Z2", 3) },
                new[] { new LoopDeclaration("A", new[] { "Z1" }), new LoopDeclaration("B", new[] { "Z2" }) });
            var drawings = Plan(bill);
            Assert.Equal(bill.RequiredBreakers, drawings.Sum(Feeds));
        }

        [Fact]
        public void MarkersFollowTheLegend()
        {
            // 3 decoders (~1470 W ≤ 1920, no count cap) ⇒ one feed of 3 drivers. ③ per driver→decoder (3),
            // ⑥ per DMX chain segment (iface→dec0 + 2 dec→dec + decN→term = 4), ⑦ one comm, ① = 1 stub + 2
            // daisies = 3, and one #16-6 (RGBW, 4-channel) homerun gauge per decoder.
            var bill = DmxSolver.Solve(Contract(0), new[] { Zone("Z1", 3) });
            var d = Assert.Single(Plan(bill));

            Assert.Equal(1, Feeds(d));
            Assert.Equal(3, Markers(d, DmxWireType.Lv(2)));  // driver → decoder
            Assert.Equal(4, Markers(d, DmxWireType.Cat6));   // DMX chain segments (n + 1)
            Assert.Equal(1, Markers(d, DmxWireType.Comm));   // interface ↔ processor
            Assert.Equal(3, Markers(d, DmxWireType.Hv));     // 1 feed stub + 2 driver-to-driver daisies
            Assert.Equal(3, Markers(d, DmxWireType.Lv(6)));  // #16-6 RGBW homerun (4 ch ⇒ 5 ⇒ 6)
        }

        [Fact]
        public void DmxChainIsVertical_AndDriverToDecoderIsHorizontal()
        {
            var bill = DmxSolver.Solve(Contract(0), new[] { Zone("Z1", 3) });
            var d = Assert.Single(Plan(bill));

            // Dashed chain segments share X (vertical); there are device+1 of them (iface→…→terminator).
            var dashedVertical = d.Wires.Where(w => w.Dashed && System.Math.Abs(w.Start.X - w.End.X) < Eps).ToList();
            Assert.Equal(bill.TotalDecoders + 1, dashedVertical.Count);

            // The comm leader is the one dashed horizontal segment.
            Assert.Single(d.Wires, w => w.Dashed && System.Math.Abs(w.Start.Y - w.End.Y) < Eps);

            // driver→decoder legs are solid and horizontal, one per decoder.
            var dToDec = d.Wires.Count(w => !w.Dashed && System.Math.Abs(w.Start.Y - w.End.Y) < Eps
                                            && System.Math.Abs(w.Start.X - w.End.X) > Eps);
            Assert.True(dToDec >= bill.TotalDecoders);
        }
    }
}
