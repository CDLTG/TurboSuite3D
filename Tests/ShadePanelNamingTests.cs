using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Shade panels are a RECOMMENDATION, named and counted like lighting panels — never read off the
    //  Revit shade panels the circuits happen to sit on. Two halves pin that:
    //
    //   • ShadeSolver.PoolByLocation — the shim's per-circuit tallies pool by PARSED location number,
    //     so circuits split across several real shade panels at one location ceil once, exactly as the
    //     panel schedule's circuits path pools them.
    //   • ControlLinkPacker.BuildDemand — shade link units take the Panel Breakdown's names (the
    //     location's letter run continued after its lighting panels), so the one-line labels them as
    //     the preview does. The regression: real shade panels named "3-B"/"3-C" drew as 3-B/3-C on the
    //     one-line, colliding with recommended lighting panel 3-B, while the preview said 3-C/3-D.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class ShadePoolByLocationTests
    {
        private static ShadeLocationTally Raw(string panel, int shades, params string[] circuits)
            => new ShadeLocationTally(panel, shades,
                circuits.Select(c => new ShadeMotorRecord(c, $"shade {c}")).ToList());

        /// <summary>Two real shade panels at one location are one pool: 4 + 3 → 7 motors → ONE
        /// QSPS-10PNL, not the two a per-panel ceil gives.</summary>
        [Fact]
        public void SplitRealPanels_AtOneLocation_PoolAndCeilOnce()
        {
            var pooled = ShadeSolver.PoolByLocation(new[] { Raw("3-B", 4), Raw("3-C", 3) });

            var loc = Assert.Single(pooled);
            Assert.Equal(7, loc.ShadeCount);
            Assert.Equal(3, PanelAllocationService.ParseLocationNumber(loc.LocationName));

            var part = Assert.Single(ShadeSolver.Solve(pooled).Parts);
            Assert.Equal(1, part.Quantity);
        }

        /// <summary>Different locations stay separate, in first-seen order.</summary>
        [Fact]
        public void DifferentLocations_StaySeparate_InFirstSeenOrder()
        {
            var pooled = ShadeSolver.PoolByLocation(new[]
            {
                Raw("3-B", 2), Raw("1-C", 5), Raw("3-C", 1), Raw("SHADE 1", 4)
            });

            Assert.Equal(new[] { 3, 1 },
                pooled.Select(t => PanelAllocationService.ParseLocationNumber(t.LocationName)));
            Assert.Equal(new[] { 3, 9 }, pooled.Select(t => t.ShadeCount));
        }

        /// <summary>A panel name with no location keeps its own raw-name tally, so it still reaches the
        /// solver as the unassigned warning rather than vanishing or merging into a real location.</summary>
        [Fact]
        public void UnparseablePanel_StaysSeparate_AndWarns()
        {
            var pooled = ShadeSolver.PoolByLocation(new[]
            {
                Raw("(unassigned)", 2), Raw("3-B", 4), Raw("(unassigned)", 1)
            });

            Assert.Equal(new[] { "(unassigned)", "3-B" }, pooled.Select(t => t.LocationName));
            Assert.Equal(new[] { 3, 4 }, pooled.Select(t => t.ShadeCount));
            Assert.NotNull(ShadeSolver.Solve(pooled).Diagnostic);
        }

        /// <summary>Merged motors read in natural circuit order across the source panels, so each
        /// QSPS-10PNL takes a contiguous chunk (M310 after M302, not between M301 and M302).</summary>
        [Fact]
        public void MergedMotors_SortNaturallyByCircuit()
        {
            var pooled = ShadeSolver.PoolByLocation(new[]
            {
                Raw("3-C", 2, "M310", "M302"),
                Raw("3-B", 1, "M301"),
            });

            Assert.Equal(new[] { "M301", "M302", "M310" },
                Assert.Single(pooled).Motors.Select(m => m.Circuit));
        }

        /// <summary>No-drift guard: the counts path (pooled tallies) and the panel schedule's circuits
        /// path recommend the same shade panels — same names, same fills — when a location's circuits
        /// are split across real panels.</summary>
        [Fact]
        public void CountsPath_MatchesCircuitsPath_ForSplitPanels()
        {
            var circuits = Enumerable.Range(1, 8).Select(i => ("3-B", $"M3{i:D2}"))
                .Concat(Enumerable.Range(9, 7).Select(i => ("3-C", $"M3{i:D2}")))
                .ToList();

            var pooled = ShadeSolver.PoolByLocation(circuits.Select(c => Raw(c.Item1, 1, c.Item2)));
            var (fromCounts, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData>(), BrandConfig.Crestron, shadeLocations: pooled);

            var shadeCircuits = circuits
                .Select(c => new ZonesCircuitData { CircuitNumber = c.Item2, PanelName = c.Item1 })
                .ToList();
            var (fromCircuits, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData>(), BrandConfig.Crestron, shadeCircuits: shadeCircuits);

            var countsPanels = fromCounts.Locations.SelectMany(l => l.ShadePanels).ToList();
            var circuitPanels = fromCircuits.Locations.SelectMany(l => l.ShadePanels).ToList();

            Assert.Equal(new[] { "3-A", "3-B" }, countsPanels.Select(p => p.PanelName));
            Assert.Equal(new[] { 10, 5 }, countsPanels.Select(p => p.ShadeCount));
            Assert.Equal(countsPanels.Select(p => p.PanelName), circuitPanels.Select(p => p.PanelName));
            Assert.Equal(countsPanels.Select(p => p.ShadeCount), circuitPanels.Select(p => p.ShadeCount));
        }
    }

    public class ShadeUnitNamingTests : ControlLinkPackerArrangeTestBase
    {
        private static List<LinkUnit> ShadeUnits(List<PanelResult> panels, BomExtras extras)
            => ControlLinkPacker.BuildDemand(panels, extras).PinnedUnits
                .Where(u => u.Category == LinkCategory.Shades)
                .ToList();

        /// <summary>The reported job: lighting 3-A, 3-B and 14 shades circuited on a real panel named
        /// "3-B". The shade units continue the letter run — 3-C, 3-D — instead of echoing "3-B".</summary>
        [Fact]
        public void ShadeUnits_ContinueLetterRun_NotTheRealPanelName()
        {
            var panels = new List<PanelResult> { Panel("3-A", modules: 4), Panel("3-B", modules: 2) };

            var units = ShadeUnits(panels, ShadeExtras(("3-B", 14)));

            Assert.Equal(new[] { "3-C", "3-D" }, units.Select(u => u.Name));
            Assert.Equal(new[] { 10, 4 }, units.Select(u => u.Loads));
        }

        /// <summary>A location with no lighting panels starts its shade run at A.</summary>
        [Fact]
        public void PureShadeLocation_StartsAtA()
        {
            var units = ShadeUnits(new List<PanelResult>(), ShadeExtras(("SHADE 2", 12)));

            Assert.Equal(new[] { "2-A", "2-B" }, units.Select(u => u.Name));
        }

        /// <summary>Each location runs its own letters off its own lighting count.</summary>
        [Fact]
        public void LetterRuns_AreIndependentPerLocation()
        {
            var panels = new List<PanelResult>
            {
                Panel("1-A", modules: 4),
                Panel("3-A", modules: 4), Panel("3-B", modules: 4),
            };

            var units = ShadeUnits(panels, ShadeExtras(("1-X", 3), ("3-X", 5)));

            Assert.Equal(new[] { "1-B", "3-C" }, units.Select(u => u.Name));
        }

        /// <summary>End-to-end no-drift guard: from the same pooled tallies, the one-line's shade units
        /// carry exactly the Panel Breakdown's shade panel names and fills, in order.</summary>
        [Fact]
        public void ShadeUnitNames_MatchPanelBreakdown()
        {
            var lighting = new List<ZonesCircuitData>
            {
                new ZonesCircuitData { CircuitNumber = "301", PanelName = "ZONE 3", DimmingType = "ELV" },
            };
            var pooled = ShadeSolver.PoolByLocation(new[]
            {
                new ShadeLocationTally("3-B", 8), new ShadeLocationTally("3-C", 7),
            });

            var (allocation, _) = PanelAllocationService.BuildPanelBreakdown(
                lighting, BrandConfig.Crestron, shadeLocations: pooled);
            var units = ShadeUnits(allocation.AllPanels,
                new BomExtras { SubsystemDemands = new[] { ShadeSolver.Solve(pooled) } });

            var preview = allocation.Locations.SelectMany(l => l.ShadePanels).ToList();
            Assert.Equal(new[] { "3-B", "3-C" }, preview.Select(p => p.PanelName));
            Assert.Equal(preview.Select(p => p.PanelName), units.Select(u => u.Name));
            Assert.Equal(preview.Select(p => p.ShadeCount), units.Select(u => u.Loads));
        }
    }
}
