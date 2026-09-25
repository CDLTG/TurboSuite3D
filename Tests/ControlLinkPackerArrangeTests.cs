using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Set 2 for ControlLinkPacker — the convention-driven ARRANGE mode (Section 1). These exercise
    //  the pooling overload Pack(demand, IReadOnlyList<ProcessorSlot>): category fan-out (keypad
    //  isolation), location pooling, soft-pool spanning, the orphan relabel pre-pass, Clear-Connect
    //  carve-before-pool, and shades as indivisible located units. The frozen Set-1 absolutes live in
    //  ControlLinkPackerTests.cs and stay untouched — they are the proof the COUNT never moved; these
    //  pin the ARRANGEMENT within that fixed count.
    //
    //  For me (Claude): the pooling overload takes one ProcessorSlot per placed processor (Location +
    //  LinkCount=2). A located unit prefers a QS link in its own location and spans elsewhere if full;
    //  keypads pour last, isolated onto a located-unit-free QS link where one exists, never onto CC-A.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public abstract class ControlLinkPackerArrangeTestBase : ControlLinkPackerTestBase
    {
        protected static IReadOnlyList<ProcessorSlot> Slots(params int[] locations)
            => locations.Select(l => new ProcessorSlot(l)).ToList();

        protected static LinkPackResult PackPooled(
            List<PanelResult> panels, IReadOnlyList<ProcessorSlot> slots,
            BomExtras? extras = null, IReadOnlyDictionary<int, int>? orphanMap = null)
        {
            var demand = ControlLinkPacker.BuildDemand(panels, extras ?? new BomExtras());
            demand = ControlLinkPacker.RelabelLocations(demand, orphanMap);
            return ControlLinkPacker.Pack(demand, slots);
        }

        /// <summary>A "Shades" subsystem demand built through the real ShadeSolver, so the per-panel
        /// QSPS-10PNL units (devices = fill+1, loads = fill) reach the packer exactly as production makes
        /// them.</summary>
        protected static BomExtras ShadeExtras(params (string location, int motors)[] locations)
            => new BomExtras
            {
                SubsystemDemands = new[]
                {
                    ShadeSolver.Solve(
                        locations.Select(l => new ShadeLocationTally(l.location, l.motors)).ToList())
                }
            };
    }

    /// <summary>Rule #2 — keypads fan out onto a spare QS link, or collapse rather than add hardware.</summary>
    public class KeypadFanOutTests : ControlLinkPackerArrangeTestBase
    {
        /// <summary>A spare QS link exists (Link 2 of a single processor, no wireless), so the keypads
        /// isolate onto it and the modules keep Link 1 to themselves.</summary>
        [Fact]
        public void KeypadsIsolateOntoASpareQsLink()
        {
            var packed = PackPooled(
                new List<PanelResult> { Panel("1-A", modules: 3) }, Slots(1),
                new BomExtras { KeypadCount = 10 });

            Assert.Equal(3, packed.Processors[0].Link1.Devices);    // modules only
            Assert.Equal(10, packed.Processors[0].Link2.Devices);   // keypads isolated onto the spare link
            Assert.Contains(LinkCategory.Keypads, packed.Processors[0].Link2.Categories);
        }

        /// <summary>The text-block case: the job has wireless, so the single processor's Link 2 goes RF
        /// (Clear Connect). There is now NO spare QS link, so the wired keypads collapse onto Link 1 with
        /// the modules — and they never ride the CC-A link (a keypad cannot).</summary>
        [Fact]
        public void KeypadsCollapseOntoTheModuleLinkWhenLink2IsRf()
        {
            var packed = PackPooled(
                new List<PanelResult> { Panel("1-A", modules: 3) }, Slots(1),
                new BomExtras { KeypadCount = 10, HybridRepeaters = Tally.Repeaters(3) });

            Assert.Equal(ProcessorLink.ClearConnectLinkType, packed.Processors[0].Link2.LinkType);
            Assert.Equal(13, packed.Processors[0].Link1.Devices);   // 3 modules + 10 keypads collapsed
            Assert.DoesNotContain(LinkCategory.Keypads, packed.Processors[0].Link2.Categories);
            Assert.Equal(3, packed.Processors[0].Link2.Repeaters);  // the RF link carries repeaters, not keypads
        }

        /// <summary>With exactly one spare QS link (one processor's Link 1 full of modules, a second
        /// processor entirely free), keypads take a spare located-unit-free link rather than the module
        /// links.</summary>
        [Fact]
        public void KeypadsPreferTheLocatedUnitFreeLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 3), Panel("2-A", modules: 3) };
            var packed = PackPooled(panels, Slots(1, 2), new BomExtras { KeypadCount = 20 });

            // Neither module link should carry keypads while a wholly empty QS link is available.
            var moduleLinks = packed.Links.Where(l => l.Categories.Contains(LinkCategory.Modules));
            Assert.All(moduleLinks, l => Assert.DoesNotContain(LinkCategory.Keypads, l.Categories));
            Assert.Equal(20, packed.Links.Where(l => l.Categories.Contains(LinkCategory.Keypads))
                                         .Sum(l => l.Devices));
        }
    }

    /// <summary>Rule #4 — located units pool by location; overflow spans rather than adding a processor.</summary>
    public class LocationPoolingTests : ControlLinkPackerArrangeTestBase
    {
        /// <summary>Each location's panel lands on a processor IN that location.</summary>
        [Fact]
        public void LocatedUnitsSelfPoolToTheirOwnLocationProcessor()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 4), Panel("2-A", modules: 4) };
            var packed = PackPooled(panels, Slots(1, 2));

            Assert.Contains("1-A", packed.Processors[0].Link1.UnitNames);   // loc 1 → processor 0
            Assert.Contains("2-A", packed.Processors[1].Link1.UnitNames);   // loc 2 → processor 1
            Assert.DoesNotContain("2-A", packed.Processors[0].Link1.UnitNames);
        }

        /// <summary>An orphan location (panels, no processor of its own) assigned to a FULL pool spans its
        /// overflow to another processor's spare capacity — the count is unchanged, no bar reddens, and
        /// nothing is double-counted.</summary>
        [Fact]
        public void OrphanAssignedToAFullPoolSpansRatherThanReddening()
        {
            // Proc at loc 1 and loc 2. Loc 1's two big panels fill its own processor's two links; the
            // orphan loc-3 panel, assigned to loc 1, cannot fit there and spans to the loc-2 processor.
            var panels = new List<PanelResult>
            {
                Panel("1-A", modules: 90, cap: 1),   // 90 devices — one per link on proc 1
                Panel("1-B", modules: 90, cap: 1),
                Panel("3-A", modules: 20, cap: 1)    // orphan: loc 3 has no processor
            };
            var orphanMap = new Dictionary<int, int> { { 3, 1 } };   // assign loc 3 → loc 1's pool

            var packed = PackPooled(panels, Slots(1, 2), orphanMap: orphanMap);

            Assert.All(packed.Links, l => Assert.False(l.IsOverCapacity));      // count unchanged, nothing over
            Assert.Contains("3-A", packed.Processors[1].Link1.UnitNames);      // spanned to the loc-2 processor
            Assert.Equal(200, packed.Links.Sum(l => l.Devices));               // 90+90+20, counted once
        }

        /// <summary>An unassigned orphan never blocks the solve: it carries a location no processor
        /// matches, so it floats into spare capacity like a location-less unit.</summary>
        [Fact]
        public void UnassignedOrphanFloatsIntoSpareCapacity()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 4), Panel("3-A", modules: 4) };
            var packed = PackPooled(panels, Slots(1));   // only a loc-1 processor; loc 3 is an orphan

            Assert.All(packed.Links, l => Assert.False(l.IsOverCapacity));
            Assert.Equal(8, packed.Links.Sum(l => l.Devices));   // both panels placed, none lost
        }

        /// <summary>A floating (unsited) interface has no pooling preference — it places into spare
        /// capacity like a location-less unit, never forcing a link.</summary>
        [Fact]
        public void FloatingInterfaceHasNoPoolingPreference()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 3), Panel("2-A", modules: 3) };
            var extras = new BomExtras
            {
                SubsystemDemands = new[]
                {
                    new ControlSubsystemDemand(
                        "DMX",
                        new List<DemandPart> { new DemandPart("QSE-CI-DMX", 1, DemandMount.LvCompartment) },
                        linkDevices: 1, linkLoads: 32)   // required but unsited → a floating interface unit
                }
            };

            var packed = PackPooled(panels, Slots(1, 2), extras);

            Assert.All(packed.Links, l => Assert.False(l.IsOverCapacity));
            Assert.Contains(packed.Links, l => l.Categories.Contains(LinkCategory.Interface));
        }

        /// <summary>Deterministic pooling (Gap #8): a multi-location job with locations out of order and
        /// size ties packs the same way every run.</summary>
        [Fact]
        public void PooledPackingIsStableAcrossRuns()
        {
            var panels = Enumerable.Range(1, 12)
                .Select(i => Panel($"{(i % 3) + 1}-{(char)('A' + i)}", modules: i % 4 + 1)).ToList();
            var extras = new BomExtras { KeypadCount = 25 };
            var slots = Slots(2, 1, 3);   // deliberately out of location order

            var first = PackPooled(panels, slots, extras).Links
                .Select(l => (l.Devices, l.Loads, l.LinkType)).ToList();
            var second = PackPooled(panels, slots, extras).Links
                .Select(l => (l.Devices, l.Loads, l.LinkType)).ToList();

            Assert.Equal(first, second);
        }
    }

    /// <summary>Gap #9 — Clear Connect is carved off the trailing links before pooling.</summary>
    public class ClearConnectCarveTests : ControlLinkPackerArrangeTestBase
    {
        /// <summary>On a two-processor job with wireless, the trailing link goes RF and pooling uses only
        /// the remaining QS links — wireless never displaces a pooled QS unit.</summary>
        [Fact]
        public void WirelessTakesTheTrailingLinkAndLeavesPoolingTheRest()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 4), Panel("2-A", modules: 4) };
            var packed = PackPooled(panels, Slots(1, 2),
                new BomExtras { HybridRepeaters = Tally.Repeaters(2) });

            // One CC-A link, carved off the very last slot (processor 2, Link 2).
            Assert.Equal(1, packed.ClearConnectLinkCount);
            Assert.Equal(ProcessorLink.ClearConnectLinkType, packed.Processors[1].Link2.LinkType);
            Assert.Equal(2, packed.Processors[1].Link2.Repeaters);

            // Both panels still pooled onto their own QS links; the CC-A link carries no modules.
            Assert.Contains("1-A", packed.Processors[0].Link1.UnitNames);
            Assert.Contains("2-A", packed.Processors[1].Link1.UnitNames);
            Assert.Empty(packed.Processors[1].Link2.UnitNames);
        }
    }

    /// <summary>Shades as indivisible, located QSPS-10PNL units (item 2a) — the physical truth the
    /// one-line depends on.</summary>
    public class ShadeArrangementTests : ControlLinkPackerArrangeTestBase
    {
        /// <summary>
        /// The concrete golden. 10 dimmer panels × 9 modules (90 devices, 360 legs) at Location 1, plus
        /// 30 shade motors at Location 1 → 3 full QSPS-10PNL (units of 11 devices / 10 legs). 123 devices
        /// total → 2 QS links, one processor. FFD (shades 11 &gt; dimmers 9) fills Link 1 to 96 (3 shade
        /// units + 7 dimmers) and Link 2 to 27 (3 dimmers), and each shade unit's 11 devices land whole on
        /// a single link — none split across the boundary.
        /// </summary>
        [Fact]
        public void ShadePanelsPackWholeAlongsideDimmers()
        {
            var panels = Enumerable.Range(0, 10)
                .Select(i => Panel($"1-{(char)('A' + i)}", modules: 9)).ToList();   // loc 1, 9 devices each
            var extras = ShadeExtras(("SHADE 1", 30));   // 3 QSPS-10PNL, all at loc 1

            var packed = PackPooled(panels, Slots(1), extras);

            Assert.Equal(2, packed.QsLinkCount);
            Assert.Equal(96, packed.Processors[0].Link1.Devices);
            Assert.Equal(27, packed.Processors[0].Link2.Devices);

            // All three shade units whole on Link 1 — none split onto Link 2.
            Assert.Equal(3, packed.Processors[0].Link1.UnitNames.Count(n => n == "SHADE 1"));
            Assert.DoesNotContain("SHADE 1", packed.Processors[0].Link2.UnitNames);
            Assert.Contains(LinkCategory.Shades, packed.Processors[0].Link1.Categories);
        }

        /// <summary>The count is unchanged by indivisibility: the recommendation off shades-as-units is
        /// the same the pour would give, because a shade contributes the identical device total either
        /// way (motors + panels).</summary>
        [Fact]
        public void ShadeUnitsDoNotMoveTheRecommendation()
        {
            var extras = ShadeExtras(("SHADE 1", 30));   // 33 devices, 30 legs — well inside one link
            var demand = ControlLinkPacker.BuildDemand(new List<PanelResult>(), extras);

            Assert.Equal(1, ControlLinkPacker.RecommendProcessors(demand));
        }

        /// <summary>Honest overflow (not a fabricated processor bump): a shade panel never silently
        /// splits, so when a fixed budget cannot hold every unit whole, its link shows over-capacity —
        /// the visible signal — rather than the packer fractioning an 11-device panel across two links.</summary>
        [Fact]
        public void ShadeUnitOverflowIsVisibleNeverSplit()
        {
            var extras = ShadeExtras(("SHADE 1", 100));   // 10 QSPS-10PNL = 110 devices
            var oneLink = new List<ProcessorSlot> { new ProcessorSlot(1, linkCount: 1) };   // a single 99-device QS link

            var packed = ControlLinkPacker.Pack(
                ControlLinkPacker.BuildDemand(new List<PanelResult>(), extras), oneLink);

            Assert.Single(packed.Links);
            Assert.Equal(110, packed.Links[0].Devices);       // all ten units whole on the one link
            Assert.True(packed.Links[0].IsOverCapacity);      // 110 > 99 — the visible over-capacity signal
        }
    }

    /// <summary>Section 2a — <see cref="PackedLink.Units"/>: the ordered, typed per-link contents the
    /// one-line planner walks. A parallel ledger — the frozen Set-1 counts (in ControlLinkPackerTests.cs)
    /// prove it moved nothing.</summary>
    public class PackedLinkUnitsTests : ControlLinkPackerArrangeTestBase
    {
        /// <summary>A link carrying a dimmer + a shade panel + collapsed keypads reports one Units entry per
        /// located unit (named, typed, in landing order) plus a single trailing keypad node — never a split
        /// or a missing keypad. (Text-block case: wireless forces Link 2 RF, so all three share Link 1.)</summary>
        [Fact]
        public void MixedLinkReportsTypedUnitsWithKeypadsCollapsedLast()
        {
            var extras = new BomExtras
            {
                KeypadCount = 10,
                HybridRepeaters = Tally.Repeaters(3),   // forces Link 2 → Clear Connect, so no spare QS link
                SubsystemDemands = new[]
                {
                    ShadeSolver.Solve(new List<ShadeLocationTally> { new ShadeLocationTally("SHADE 1", 5) })
                },
            };

            var packed = PackPooled(new List<PanelResult> { Panel("1-A", modules: 3) }, Slots(1), extras);
            var link1 = packed.Processors[0].Link1;   // the sole QS link — carries everything

            Assert.Equal(3, link1.Units.Count);

            var modules = Assert.Single(link1.Units, u => u.Category == LinkCategory.Modules);
            Assert.Equal("1-A", modules.Name);
            Assert.Equal(3, modules.Devices);

            var shade = Assert.Single(link1.Units, u => u.Category == LinkCategory.Shades);
            Assert.Equal("SHADE 1", shade.Name);
            Assert.Equal(6, shade.Devices);           // one QSPS-10PNL: 5 motors + 1 panel device

            // Keypads collapse to exactly one synthetic node, and it lands LAST (poured after located units).
            var keypads = Assert.Single(link1.Units, u => u.Category == LinkCategory.Keypads);
            Assert.Same(keypads, link1.Units[link1.Units.Count - 1]);
            Assert.Equal("Keypads", keypads.Name);
            Assert.Equal(10, keypads.Devices);
        }

        /// <summary>Keypads collapse to ONE keypad Units node per link, carrying that link's keypad device
        /// share and no loads — here isolated alone on the spare QS link.</summary>
        [Fact]
        public void KeypadsCollapseToOneUnitPerLinkWithTheirDeviceShare()
        {
            var packed = PackPooled(
                new List<PanelResult> { Panel("1-A", modules: 3) }, Slots(1),
                new BomExtras { KeypadCount = 10 });

            var moduleUnit = Assert.Single(packed.Processors[0].Link1.Units);
            Assert.Equal(LinkCategory.Modules, moduleUnit.Category);
            Assert.Equal("1-A", moduleUnit.Name);

            var keypadUnit = Assert.Single(packed.Processors[0].Link2.Units);   // isolated onto the spare link
            Assert.Equal(LinkCategory.Keypads, keypadUnit.Category);
            Assert.Equal("Keypads", keypadUnit.Name);
            Assert.Equal(10, keypadUnit.Devices);
            Assert.Equal(0, keypadUnit.Loads);
        }
    }
}
