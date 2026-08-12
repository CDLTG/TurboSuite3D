using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliPlacementMapper"/> — the persisted-loops → panel-placement boundary
    /// (Phase 3e). Pins the split the plan mandates: an assigned loop lands in its ZONE N with a load count
    /// summed over its zones, an unassigned loop (AssignedZone 0) is warned-not-placed, and a zero-load loop
    /// is dropped from both (it orders no module, so there is nothing to place or warn). Reconciliation is
    /// the same shared pass DaliStateMapper runs, so the load-affecting rules (renamed zone dropped,
    /// contested zone first-wins) show through here too.
    /// </summary>
    public class DaliPlacementMapperTests
    {
        private static DaliLoopDto Loop(string name, int order, int assignedZone, params string[] zones) =>
            new DaliLoopDto
            {
                LoopId = name,
                Name = name,
                Order = order,
                AssignedZone = assignedZone,
                ZoneValues = zones.ToList()
            };

        private static Dictionary<string, int> Loads(params (string Zone, int Count)[] pairs) =>
            pairs.ToDictionary(p => p.Zone, p => p.Count);

        [Fact]
        public void NullLoops_YieldEmptyPlacement()
        {
            var p = DaliPlacementMapper.Build(null, Loads(("A", 3)));
            Assert.Empty(p.ByZone);
            Assert.Empty(p.Unassigned);
        }

        [Fact]
        public void AssignedLoop_LandsInItsZone_WithSummedLoadCount()
        {
            var loops = new[] { Loop("Kitchen", 1, assignedZone: 2, "K1", "K2") };
            var loads = Loads(("K1", 3), ("K2", 5));

            var p = DaliPlacementMapper.Build(loops, loads);

            Assert.Empty(p.Unassigned);
            var zone = Assert.Single(p.ByZone);
            Assert.Equal(2, zone.Key);
            var module = Assert.Single(zone.Value);
            Assert.Equal("Kitchen", module.LoopName);
            Assert.Equal(8, module.LoadCount);           // 3 + 5, summed across the loop's zones
        }

        [Fact]
        public void TwoLoopsSameZone_BothOccupyThatZone()
        {
            var loops = new[]
            {
                Loop("A", 1, assignedZone: 1, "Za"),
                Loop("B", 2, assignedZone: 1, "Zb"),
            };
            var loads = Loads(("Za", 2), ("Zb", 4));

            var p = DaliPlacementMapper.Build(loops, loads);

            var zone = Assert.Single(p.ByZone);
            Assert.Equal(1, zone.Key);
            Assert.Equal(new[] { "A", "B" }, zone.Value.Select(m => m.LoopName));
        }

        [Fact]
        public void UnassignedLoopWithLoads_IsWarnedNotPlaced()
        {
            var loops = new[] { Loop("Orphan", 1, assignedZone: 0, "Z") };
            var loads = Loads(("Z", 6));

            var p = DaliPlacementMapper.Build(loops, loads);

            Assert.Empty(p.ByZone);
            var orphan = Assert.Single(p.Unassigned);
            Assert.Equal("Orphan", orphan.LoopName);
            Assert.Equal(6, orphan.LoadCount);
        }

        [Fact]
        public void ZeroLoadLoop_IsDroppedFromBoth()
        {
            // The loop's zone exists (so it survives reconciliation) but carries no DALI fixtures.
            var loops = new[] { Loop("Empty", 1, assignedZone: 3, "Z") };
            var loads = Loads(("Z", 0));

            var p = DaliPlacementMapper.Build(loops, loads);

            Assert.Empty(p.ByZone);
            Assert.Empty(p.Unassigned);
        }

        [Fact]
        public void RenamedZone_DropsFromTheLoadSum()
        {
            var loops = new[] { Loop("Kitchen", 1, assignedZone: 1, "Live", "Renamed") };
            var loads = Loads(("Live", 4));   // "Renamed" is not a current zone

            var p = DaliPlacementMapper.Build(loops, loads);

            var module = Assert.Single(p.ByZone[1]);
            Assert.Equal(4, module.LoadCount);   // only the live zone counts
        }

        [Fact]
        public void ContestedZone_CountsOnlyForTheFirstLoop()
        {
            var loops = new[]
            {
                Loop("First", 1, assignedZone: 1, "Shared"),
                Loop("Second", 2, assignedZone: 2, "Shared", "Own"),
            };
            var loads = Loads(("Shared", 10), ("Own", 3));

            var p = DaliPlacementMapper.Build(loops, loads);

            Assert.Equal(10, Assert.Single(p.ByZone[1]).LoadCount);   // First wins "Shared"
            Assert.Equal(3, Assert.Single(p.ByZone[2]).LoadCount);    // Second keeps only "Own"
        }

        [Fact]
        public void NegativeZone_IsTreatedAsUnassigned()
        {
            var loops = new[] { Loop("Weird", 1, assignedZone: -1, "Z") };
            var p = DaliPlacementMapper.Build(loops, Loads(("Z", 2)));

            Assert.Empty(p.ByZone);
            Assert.Single(p.Unassigned);
        }
    }
}
