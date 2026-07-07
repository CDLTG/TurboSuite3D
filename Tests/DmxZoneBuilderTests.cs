using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxZoneBuilder"/> — the Revit-readings → engine-zones boundary.
    /// Verifies grouping by Control Zone value, flat-cluster default, unassigned counting, and that the
    /// produced zones feed the engine solver unchanged.
    /// </summary>
    public class DmxZoneBuilderTests
    {
        private static DmxFixtureReading Fix(string zone, int ch, double len, double wpf = 5.2, long id = 0) =>
            new DmxFixtureReading { ElementId = id, ControlZone = zone, Channels = ch, LengthFt = len, WattsPerFt = wpf };

        private static DmxClusterDto Cluster(string name, string zone, params long[] runs) =>
            new DmxClusterDto { ClusterId = name, Name = name, ZoneValue = zone, RunElementIds = runs.ToList() };

        [Fact]
        public void GroupsFixturesByControlZoneValue()
        {
            var result = DmxZoneBuilder.Build(new[]
            {
                Fix("Foyer Cove", 4, 10), Fix("Foyer Cove", 4, 12),
                Fix("Bar Backlight", 4, 8),
            });

            Assert.Equal(2, result.Zones.Count);
            Assert.Equal(new[] { "Foyer Cove", "Bar Backlight" }, result.ZoneNames);
            var foyer = result.Zones.Single(z => z.ZoneName == "Foyer Cove");
            Assert.Equal(2, foyer.Runs.Count);
        }

        [Fact]
        public void DefaultsToOneFlatClusterPerZone()
        {
            var result = DmxZoneBuilder.Build(new[] { Fix("Z1", 4, 10), Fix("Z1", 4, 10) });
            var zone = result.Zones.Single();
            Assert.Single(zone.Clusters);          // flat: one cluster
            Assert.Equal(2, zone.Clusters[0].Runs.Count);
        }

        [Fact]
        public void EmptyOrWhitespaceZoneCountsAsUnassignedAndIsNotSolved()
        {
            var result = DmxZoneBuilder.Build(new[]
            {
                Fix("Z1", 4, 10), Fix("", 4, 10), Fix("   ", 4, 10),
            });

            Assert.Equal(2, result.UnassignedFixtures);
            Assert.Single(result.Zones);
        }

        [Fact]
        public void GroupingIsCaseInsensitiveAndOrderPreserving()
        {
            var result = DmxZoneBuilder.Build(new[] { Fix("Cove", 4, 10), Fix("cove", 4, 5), Fix("Wall", 4, 6) });
            Assert.Equal(new[] { "Cove", "Wall" }, result.ZoneNames);
            Assert.Equal(2, result.Zones.Single(z => z.ZoneName == "Cove").Runs.Count);
        }

        [Fact]
        public void BuiltZonesSolveThroughTheEngine()
        {
            var result = DmxZoneBuilder.Build(new[] { Fix("Z1", 4, 20), Fix("Z2", 4, 15) });
            var contract = DmxContractBuilder.Build(
                DmxProfile.Lutron, new DmxJobSettings(),
                new[] { new DmxDecoderCandidate { Name = "4ch", MaxOutputs = 4, MaxAmpsPerOutput = 10, MaxWatts = 960 } },
                new[] { new DmxDriverCandidate { Name = "MD", RatedWatts = 288, OperatingVolts = 24, DeratingFactorRaw = 0.8 } });

            var bill = DmxSolver.Solve(contract, result.Zones);

            Assert.Equal(2, bill.Zones.Count);
            Assert.True(bill.TotalDecoders >= 2);
        }

        // ── Cluster sub-builder: per-zone partition by fixture ElementId ───────────────────────

        [Fact]
        public void ClustersPartitionZoneIntoNamedClustersPlusResidual()
        {
            var fixtures = new[] { Fix("Z1", 4, 10, id: 1), Fix("Z1", 4, 10, id: 2), Fix("Z1", 4, 10, id: 3) };
            var clusters = new[] { Cluster("East", "Z1", 1, 2) };

            var zone = DmxZoneBuilder.Build(fixtures, clusters).Zones.Single();

            Assert.Equal(2, zone.Clusters.Count);                 // East + residual
            Assert.Equal("East", zone.Clusters[0].Name);
            Assert.Equal(2, zone.Clusters[0].Runs.Count);
            Assert.Equal(DmxZoneBuilder.ResidualClusterName, zone.Clusters[1].Name);
            Assert.Single(zone.Clusters[1].Runs);                 // run 3 unclustered
        }

        [Fact]
        public void RunListedInTwoClustersBindsToTheLast()
        {
            var fixtures = new[] { Fix("Z1", 4, 10, id: 1), Fix("Z1", 4, 10, id: 2) };
            var clusters = new[] { Cluster("A", "Z1", 1, 2), Cluster("B", "Z1", 2) };  // run 2 in both

            var zone = DmxZoneBuilder.Build(fixtures, clusters).Zones.Single();

            Assert.Single(zone.Clusters.Single(c => c.Name == "A").Runs);  // run 1 only
            Assert.Single(zone.Clusters.Single(c => c.Name == "B").Runs);  // run 2 (last wins)
            Assert.DoesNotContain(zone.Clusters, c => c.Name == DmxZoneBuilder.ResidualClusterName);
        }

        [Fact]
        public void ClusterRunIdsNotInTheZoneAreIgnored()
        {
            var fixtures = new[] { Fix("Z1", 4, 10, id: 1), Fix("Z1", 4, 10, id: 2) };
            var clusters = new[] { Cluster("A", "Z1", 1, 99) };   // 99 isn't a fixture in this zone

            var zone = DmxZoneBuilder.Build(fixtures, clusters).Zones.Single();

            Assert.Single(zone.Clusters.Single(c => c.Name == "A").Runs);          // only run 1
            Assert.Single(zone.Clusters.Single(c => c.Name == DmxZoneBuilder.ResidualClusterName).Runs); // run 2
        }

        [Fact]
        public void ClustersForAnotherZoneDontAffectThisOne()
        {
            var fixtures = new[] { Fix("Z1", 4, 10, id: 1), Fix("Z1", 4, 10, id: 2) };
            var clusters = new[] { Cluster("X", "Z2", 1, 2) };    // wrong zone

            var zone = DmxZoneBuilder.Build(fixtures, clusters).Zones.Single();

            Assert.Single(zone.Clusters);                          // flat — the Z2 cluster is irrelevant
            Assert.Equal(2, zone.Clusters[0].Runs.Count);
        }
    }
}
