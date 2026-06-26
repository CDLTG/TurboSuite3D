using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The drawn-correctly gate (Design §0b): the engine refuses to silently cut a drawn run. A run too
    /// long for a single feed is flagged (batched, located, with the min split) and the whole solve is
    /// refused; runs that fit are grouped WHOLE, never cut. The cap is the coupled effective feed
    /// (decoder C1/C2 ∧ largest driver × derate), so the derate INPUT moves the threshold.
    /// </summary>
    public class ValidationTests
    {
        private const double V = 24.0;

        private static DriverType[] Drivers(double derate) => new[]
        {
            new DriverType("480", 480.0, V, derate),
            new DriverType("600", 600.0, V, derate),
        };

        private static DmxContract Contract(double derate) => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
            driverPool: Drivers(derate),
            systemVolts: V, channelCeiling: 32, reservedChannels: 0, maxDevicesPerSegment: 32);

        [Fact]
        public void OverCapRun_AbortsTheSolve_WithRedrawFlag()
        {
            // 200 ft = 1040 W on a 600 W (no-derate) feed ⇒ ≥ 2 pieces, ≤ 115.4 ft each.
            var zones = new[] { new ZoneDesign("Lobby Cove", new[] { new TapeRun(200.0, 5.2, 4) }) };

            var ex = Assert.Throws<OverCapRunsException>(() => DmxSolver.Solve(Contract(1.00), zones));

            var v = Assert.Single(ex.Violations);
            Assert.Equal("Lobby Cove", v.ZoneName);
            Assert.Equal(0, v.RunIndex);
            Assert.Equal(1040.0, v.Watts, precision: 0);
            Assert.Equal(600.0, v.CapWatts, precision: 0);
            Assert.Equal(2, v.MinPieces);
            Assert.Equal(115.4, v.MaxLengthFt, precision: 1);
        }

        [Fact]
        public void Derate_LowersTheCap_AndRaisesTheMinSplit()
        {
            // Same 1040 W run, derate 0.80 ⇒ cap 480 ⇒ ≥ 3 pieces, ≤ 92.3 ft each.
            var zones = new[] { new ZoneDesign("Lobby Cove", new[] { new TapeRun(200.0, 5.2, 4) }) };

            var ex = Assert.Throws<OverCapRunsException>(() => DmxSolver.Solve(Contract(0.80), zones));
            var v = Assert.Single(ex.Violations);

            Assert.Equal(480.0, v.CapWatts, precision: 0);
            Assert.Equal(3, v.MinPieces);
            Assert.Equal(92.3, v.MaxLengthFt, precision: 1);
        }

        [Fact]
        public void RunExactlyAtCap_IsAllowed()
        {
            // 600 W exactly on a 600 W feed — boundary fits, no flag.
            var zones = new[] { new ZoneDesign("Edge", new[] { new TapeRun(600.0 / 5.2, 5.2, 4) }) };
            var bill = DmxSolver.Solve(Contract(1.00), zones);
            Assert.Equal(1, bill.TotalDecoders);
        }

        [Fact]
        public void InCapRuns_AreGroupedWhole_NeverCut()
        {
            // Three runs that each fit and together exceed one feed ⇒ grouped onto whole-run decoders,
            // watts conserved, no run cut: piece count across all decoders == run count.
            var runs = new[]
            {
                new TapeRun(300.0 / 5.2, 5.2, 4),
                new TapeRun(250.0 / 5.2, 5.2, 4),
                new TapeRun(200.0 / 5.2, 5.2, 4),
            };
            var bill = DmxSolver.Solve(Contract(1.00), new[] { new ZoneDesign("Cove", runs) });

            int pieces = bill.Zones.Single().Decoders.Sum(d => d.Decoder.PieceWatts.Count);
            Assert.Equal(runs.Length, pieces); // every piece is a whole run — nothing was cut
        }

        [Fact]
        public void MultipleOffenders_AcrossZones_AreAllReportedAtOnce()
        {
            var zones = new[]
            {
                new ZoneDesign("A", new[] { new TapeRun(200.0, 5.2, 4) }), // 1040 W — over
                new ZoneDesign("B", new[] { new TapeRun(40.0,  5.2, 4) }), //  208 W — fine
                new ZoneDesign("C", new[] { new TapeRun(150.0, 5.2, 4) }), //  780 W — over
            };

            var ex = Assert.Throws<OverCapRunsException>(() => DmxSolver.Solve(Contract(1.00), zones));

            Assert.Equal(2, ex.Violations.Count);
            Assert.Equal(new[] { "A", "C" }, ex.Violations.Select(v => v.ZoneName).ToArray());
        }

        [Fact]
        public void FindOverCapRuns_IsEmpty_WhenEverythingFits()
        {
            var zones = new[] { new ZoneDesign("OK", new[] { new TapeRun(100.0, 5.2, 4) }) }; // 520 W ≤ 600
            Assert.Empty(DmxValidator.FindOverCapRuns(Contract(1.00), zones));
        }
    }
}
