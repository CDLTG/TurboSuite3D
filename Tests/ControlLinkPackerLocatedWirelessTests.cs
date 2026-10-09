using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  F3 — per-location Clear Connect sizing (Gap #9). A CC-A link is budgeted per repeater LOCATION,
    //  indivisible at four repeaters/link: Σ_loc ceil(repeaters_loc / 4), with the 99-device cap as
    //  the backstop — exactly as located shade/dimmer panels pool, rather than the old global pooling
    //  that quietly assumed repeaters in different locations could share a link.
    //
    //  The guard that matters: with NO located repeater data the packer falls back to the global
    //  pooling on BomExtras.HybridRepeaterCount/WirelessDeviceCount, byte-identical to before — pinned
    //  here and by every test in ControlLinkPackerTests that uses Tally.Repeaters(n).
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class ControlLinkPackerLocatedWirelessTests : ControlLinkPackerTestBase
    {
        private static int CcaLinks(LinkPackResult r) => r.ClearConnectLinkCount;

        /// <summary>
        /// Two repeaters in location 1 and two in location 2. Each location ceils to its own whole
        /// link (ceil(2/4) = 1), so the job needs TWO CC-A links — not the one the global pool gives
        /// (ceil(4/4) = 1). This is the Gap #9 growth: a repeater cannot share a link across locations.
        /// </summary>
        [Fact]
        public void RepeatersInTwoLocationsEachTakeTheirOwnLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(4),   // BOM catalog count — 4 repeaters ordered
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("1-REP1", repeaters: 2),
                    Tally.Loc("2-REP1", repeaters: 2)
                }
            };

            Assert.Equal(2, CcaLinks(Pack(panels, extras)));
        }

        /// <summary>The same four repeaters pooled globally (no location data) stay one link — the
        /// byte-identical fallback. The ONLY difference from the test above is the location split.</summary>
        [Fact]
        public void SameRepeatersWithNoLocationDataPoolToOneLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            Assert.Equal(1, CcaLinks(Pack(panels, new BomExtras { HybridRepeaters = Tally.Repeaters(4) })));
        }

        /// <summary>The processor recommendation follows the per-location count: 1 QS link for the
        /// panel + 2 CC-A links = 3 links ⇒ 2 processors. Globally pooled it is 1 QS + 1 CC-A = 2
        /// links ⇒ 1 processor, so the split is what forces the second processor.</summary>
        [Fact]
        public void RecommendProcessorsFollowsPerLocationSizing()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };

            int located = ControlLinkPacker.RecommendProcessors(ControlLinkPacker.BuildDemand(panels,
                new BomExtras
                {
                    HybridRepeaters = Tally.Repeaters(4),
                    RepeaterLocations = new List<RepeaterLocationTally>
                    {
                        Tally.Loc("1-REP1", repeaters: 2),
                        Tally.Loc("2-REP1", repeaters: 2)
                    }
                }));
            Assert.Equal(2, located);

            Assert.Equal(1, Recommend(panels, new BomExtras { HybridRepeaters = Tally.Repeaters(4) }));
        }

        /// <summary>Five repeaters in one location is two links there (ceil(5/4)); the overflow
        /// repeater piles onto the location's second link.</summary>
        [Fact]
        public void FiveRepeatersInOneLocationIsTwoLinks()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(5),
                RepeaterLocations = new List<RepeaterLocationTally> { Tally.Loc("1-REP1", repeaters: 5) }
            };

            Assert.Equal(2, CcaLinks(Pack(panels, extras)));
        }

        /// <summary>The 99-device cap still binds within a location: one repeater serving 100 wireless
        /// keypad devices needs a second link even though one repeater fits a single link four times
        /// over. The repeater cap and the device cap bind independently, per location.</summary>
        [Fact]
        public void DeviceCapBindsWithinALocation()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var keypads = Enumerable.Range(1, 100).Select(i => Tally.Keypad($"{i}", location: 1)).ToArray();
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(1),
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("1-REP1", repeaters: 1, keypads)   // 1 + 100 = 101 devices > 99
                }
            };

            Assert.Equal(2, CcaLinks(Pack(panels, extras)));
        }

        /// <summary>A location's wireless keypads ride that location's CC-A link, and the link carries
        /// their records for the one-line fan (F4). A keypad never lands on the QS link.</summary>
        [Fact]
        public void WirelessKeypadsRideTheirRepeaterLocationLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(1),
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("1-REP1", repeaters: 1, Tally.Keypad("K1", 1), Tally.Keypad("K2", 1))
                }
            };

            var packed = Pack(panels, extras);
            var cca = packed.Links.Single(l => l.IsClearConnect);
            Assert.Equal(3, cca.Devices);               // 1 repeater + 2 keypads
            Assert.Equal(1, cca.Repeaters);
            Assert.Equal(2, cca.WirelessKeypadRecords.Count);
            Assert.Equal(new[] { "K1", "K2" }, cca.WirelessKeypadRecords.Select(k => k.SwitchId));
            Assert.Equal(1, packed.Links.First(l => !l.IsClearConnect).Devices);   // panel module only
        }

        /// <summary>An unlocated repeater tally (panel name with no parseable location) drives no
        /// link — it is a warning upstream, exactly as an unassigned shade is. With only unlocated
        /// tallies the job falls back to the global pool on the scalar count.</summary>
        [Fact]
        public void UnlocatedRepeaterTallyFallsBackToGlobalScalar()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(2),
                RepeaterLocations = new List<RepeaterLocationTally> { Tally.Loc("REP", repeaters: 2) }
            };

            // "REP" has no leading location number → dropped from the located path → global pool of 2.
            Assert.Equal(1, CcaLinks(Pack(panels, extras)));
        }

        /// <summary>Two repeaters named in the SAME location ("2-REP1", "2-REP2") pool into one CC-A
        /// link — ceil(2/4) = 1 — not two. The shim keys tallies by location number, so a location
        /// with several repeaters is one pool. (Regression: keying by panel name split them.)</summary>
        [Fact]
        public void MultipleRepeatersInOneLocationAreOneLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(2),
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("2-REP1", repeaters: 1),
                    Tally.Loc("2-REP2", repeaters: 1)
                }
            };

            Assert.Equal(1, CcaLinks(Pack(panels, extras)));
        }

        /// <summary>The orphan→host relabel folds an orphaned location's repeaters into the host's CC-A
        /// pool: Loc 3 (1 repeater + 3 keypads) orphaned to Loc 2 (1 repeater) shares ONE link
        /// (2 repeaters ≤ 4, 5 devices ≤ 99) instead of forcing a second — the scenario that motivated
        /// the fix. Without the relabel it would be two links.</summary>
        [Fact]
        public void OrphanedLocationRepeatersFoldIntoTheHostLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(2),
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("2-REP1", repeaters: 1, Tally.Keypad("K1", 2), Tally.Keypad("K2", 2)),
                    Tally.Loc("3-REP1", repeaters: 1, Tally.Keypad("K3", 3), Tally.Keypad("K4", 3), Tally.Keypad("K5", 3))
                },
                OrphanToHost = new Dictionary<int, int> { { 3, 2 } }   // Loc 3 wired to Loc 2's processor
            };

            var demand = ControlLinkPacker.RelabelLocations(
                ControlLinkPacker.BuildDemand(panels, extras), extras.OrphanToHost);
            var packed = ControlLinkPacker.Pack(demand, availableLinks: null);

            var cca = packed.Links.Single(l => l.IsClearConnect);
            Assert.Equal(1, packed.ClearConnectLinkCount);
            Assert.Equal(2, cca.Repeaters);                 // both repeaters on the one link
            Assert.Equal(7, cca.Devices);                   // 2 repeaters + 5 keypads
            Assert.Equal(5, cca.WirelessKeypadRecords.Count);
        }

        /// <summary>Not orphaned, the same two locations stay two links — the fold happens only on the
        /// designer's explicit orphan→host assignment, never on its own.</summary>
        [Fact]
        public void WithoutOrphanAssignmentTheTwoLocationsStayTwoLinks()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.Repeaters(2),
                RepeaterLocations = new List<RepeaterLocationTally>
                {
                    Tally.Loc("2-REP1", repeaters: 1),
                    Tally.Loc("3-REP1", repeaters: 1)
                }
            };

            var demand = ControlLinkPacker.RelabelLocations(
                ControlLinkPacker.BuildDemand(panels, extras), orphanToHost: null);
            Assert.Equal(2, ControlLinkPacker.Pack(demand, availableLinks: null).ClearConnectLinkCount);
        }

        /// <summary>The processor recommendation applies the SAME orphan fold as the bars, so the two
        /// can't disagree. Loc 2 (2 repeaters) + Loc 3 (2 repeaters), Loc 3 orphaned to Loc 2:
        /// unfolded that is 2 CC-A links (1 QS + 2 CC-A = 3 links ⇒ 2 processors); folded, Loc 2 holds
        /// 4 repeaters on ONE link (1 QS + 1 CC-A = 2 links ⇒ 1 processor). The recommendation must
        /// return the folded 1.</summary>
        [Fact]
        public void RecommendationFoldsOrphansLikeTheBars()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var repeaterLocations = new List<RepeaterLocationTally>
            {
                Tally.Loc("2-REP1", repeaters: 2),
                Tally.Loc("3-REP1", repeaters: 2)
            };

            // Without the orphan map the two locations stay separate → 2 processors.
            Assert.Equal(2, ControlBomBuilder.CalculateRecommendedProcessors(panels,
                new BomExtras { HybridRepeaters = Tally.Repeaters(4), RepeaterLocations = repeaterLocations }));

            // Orphaning Loc 3 → Loc 2 folds the repeaters onto one link → 1 processor.
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(panels,
                new BomExtras
                {
                    HybridRepeaters = Tally.Repeaters(4),
                    RepeaterLocations = repeaterLocations,
                    OrphanToHost = new Dictionary<int, int> { { 3, 2 } }
                }));
        }
    }
}
