using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxZoneBuilder"/> — the Revit-readings → engine-zones boundary (Phase 1).
    /// Verifies grouping by Control Zone value, flat-cluster default, unassigned counting, and that the
    /// produced zones feed the engine solver unchanged.
    /// </summary>
    public class DmxZoneBuilderTests
    {
        private static DmxFixtureReading Fix(string zone, int ch, double len, double wpf = 5.2) =>
            new DmxFixtureReading { ControlZone = zone, Channels = ch, LengthFt = len, WattsPerFt = wpf };

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
    }
}
