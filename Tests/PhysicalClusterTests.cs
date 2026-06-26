using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Physical clusters (§8d): decoders pack PER cluster (a decoder can't reach across the room), but
    /// addressing stays PER zone (one mirrored address). This is what lets the engine say "9 decoders,
    /// 2 channels" for three same-color walls — a combination neither pure zoning nor pure pooling gives.
    /// </summary>
    public class PhysicalClusterTests
    {
        private const double V = 24.0;

        private static DmxContract Contract() => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A },
            driverPool: new[] { new DriverType("MD", 480, V, 0.85), new DriverType("ME", 600, V, 0.85) },
            systemVolts: V, channelCeiling: 512, reservedChannels: 0, maxDevicesPerSegment: 32);

        // TW (2-ch) sheets at 17.2 W each (wattsPerFt = 1 ⇒ length is watts).
        private static TapeRun[] Sheets(int n) => Enumerable.Range(0, n).Select(_ => new TapeRun(17.2, 1.0, 2)).ToArray();

        [Fact]
        public void Clusters_PackDecodersPerCluster_ButShareOneAddress()
        {
            // Three same-color walls = one control zone, three physical clusters. At 27 sheets/decoder:
            // 72→3, 60→3, 72→3 = 9 decoders. One mirrored address pair ⇒ 2 channels.
            var zone = new ZoneDesign("All Walls", new[]
            {
                new RunCluster("East",  Sheets(72)),
                new RunCluster("North", Sheets(60)),
                new RunCluster("West",  Sheets(72)),
            });

            var bill = DmxSolver.Solve(Contract(), new[] { zone });

            Assert.Equal(9, bill.TotalDecoders);
            Assert.Equal(2, bill.TotalChannels);                 // addressing is per-zone, not per-cluster
            Assert.Equal(1, bill.InterfaceCount);
            Assert.Equal(new[] { 3, 3, 3 }, bill.Zones.Single().Clusters.Select(c => c.DecoderCount).ToArray());
        }

        [Fact]
        public void OneFlatCluster_PoolsAcrossWalls_FewerDecoders_GeometryBlind()
        {
            // The SAME 204 sheets with no physical partition pool globally ⇒ 8 — the geometry-blind
            // optimum that isn't buildable when the walls are separate locations (§8d / §3b).
            var zone = new ZoneDesign("All Walls", Sheets(204));

            var bill = DmxSolver.Solve(Contract(), new[] { zone });

            Assert.Equal(8, bill.TotalDecoders); // 204 / 27 = 8, vs 9 when partitioned per wall
            Assert.Equal(2, bill.TotalChannels);
        }

        [Fact]
        public void Parse_AttachesClustersToZone()
        {
            const string text = @"
wattsPerFt = 1
decoder = 4ch outputs:4 amps:10 watts:960
driver = MD 480 24 0.85
zone = All Walls | 2
cluster = All Walls | East  | 17.2 ×72
cluster = All Walls | North | 17.2 ×60
cluster = All Walls | West  | 17.2 ×72
";
            var s = ScenarioParser.Parse(text);

            var z = Assert.Single(s.Zones);
            Assert.Equal(3, z.Clusters.Count);
            Assert.Equal(204, z.Runs.Count); // flattened across clusters
            Assert.Equal(new[] { "East", "North", "West" }, z.Clusters.Select(c => c.Name).ToArray());
        }
    }
}
