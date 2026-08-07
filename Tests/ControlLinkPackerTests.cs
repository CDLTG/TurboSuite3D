using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for ControlLinkPacker (Core/Zones/Services/ControlLinkPacker.cs) — the single
    //  computation of control-link demand, replacing two that disagreed (the BOM's pooled
    //  CalculateRecommendedProcessors and LinkAssignmentService's forward-only first-fit).
    //
    //  The invariant this suite exists to protect: the Panel Breakdown's capacity bars and the BOM's
    //  processor recommendation are the same function asked two questions. If a bar is over capacity,
    //  the BOM recommends more processors; if it recommends more, some bar is over. A test that pins
    //  one side without the other is not protecting the invariant.
    //
    //  For me (Claude): a PanelResult's DeviceCount = Modules.Count and LoadCount = Σ ModuleCapacity
    //  (NAMEPLATE outputs, not circuits used — see NameplateLoadsNotUsedSlots). Caps are
    //  ProcessorLink.MaxDevices=99, MaxLoads=512, MaxRepeatersPerClearConnectLink=4. Real Lutron
    //  panels top out at 9 modules × 4 outputs, so units are small relative to a link — which is why
    //  the pooled-vs-packed divergence needs a large job to show up at all. Derivations inline.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public abstract class ControlLinkPackerTestBase
    {
        /// <summary>A panel with <paramref name="modules"/> modules of capacity <paramref name="cap"/>
        /// → DeviceCount=modules, LoadCount=modules*cap.</summary>
        protected static PanelResult Panel(string name, int modules, int cap = 4)
        {
            var panel = new PanelResult { PanelName = name };
            for (int i = 0; i < modules; i++)
                panel.Modules.Add(new ModuleResult { ModuleCapacity = cap });
            return panel;
        }

        /// <summary>Gives the panel a single LV compartment holding <paramref name="device"/>.</summary>
        protected static PanelResult WithCompartment(PanelResult panel, string device)
        {
            panel.SpecialCompartmentPanelSizes = new HashSet<int> { 4 };
            panel.SelectedPanelSize = 4;
            panel.SelectedSpecialDevice = device;
            return panel;
        }

        protected static ControlSubsystemDemand DmxDemand(int interfaces, int channels)
            => new ControlSubsystemDemand(
                "DMX",
                new List<DemandPart> { new DemandPart("QSE-CI-DMX", interfaces, DemandMount.LvCompartment) },
                linkDevices: interfaces,
                linkLoads: channels);

        protected static int Recommend(List<PanelResult> panels, BomExtras? extras = null)
            => ControlLinkPacker.RecommendProcessors(
                ControlLinkPacker.BuildDemand(panels, extras ?? new BomExtras()));

        protected static LinkPackResult Pack(List<PanelResult> panels, BomExtras? extras = null,
            int? availableLinks = null)
            => ControlLinkPacker.Pack(
                ControlLinkPacker.BuildDemand(panels, extras ?? new BomExtras()), availableLinks);
    }

    /// <summary>A panel is wired to ONE link. This is the assumption the old pooled arithmetic
    /// silently broke, and it can only ever break it in the under-reporting direction.</summary>
    public class IndivisiblePanelTests : ControlLinkPackerTestBase
    {
        /// <summary>
        /// 33 panels of 6 modules: 198 devices, 792 loads.
        ///
        /// Pooled, the old way: max(ceil(198/99), ceil(792/512)) = 2 links ⇒ 1 processor. That answer
        /// requires splitting a panel across two links, which is not a thing.
        ///
        /// Packed: a link holds 16 of these panels (16×6 = 96 devices; the 17th would be 102), so
        /// 16 + 16 + 1 = 3 links ⇒ 2 processors. The job is short a processor under the old math.
        /// </summary>
        [Fact]
        public void PanelsPackAsWholeUnits_NotAsPooledDeviceTotals()
        {
            var panels = Enumerable.Range(1, 33).Select(i => Panel($"P{i:00}", modules: 6)).ToList();

            Assert.Equal(198, panels.Sum(p => p.DeviceCount));
            Assert.Equal(792, panels.Sum(p => p.LoadCount));

            var packed = Pack(panels);
            Assert.Equal(3, packed.QsLinkCount);
            Assert.Equal(2, Recommend(panels));
        }

        /// <summary>Loads follow module NAMEPLATE capacity, not circuits assigned. A 4-output module
        /// holding one circuit still presents four switch legs to the link, so a half-empty panel
        /// costs the same as a full one. Deliberate — not an artefact of LoadCount being the
        /// convenient field.</summary>
        [Fact]
        public void NameplateLoadsNotUsedSlots()
        {
            var panel = Panel("1-A", modules: 1, cap: 4);
            panel.Modules[0].CircuitNumbers.Add("1");   // one circuit in a four-output module

            Assert.Equal(1, panel.Modules[0].UsedSlots);
            Assert.Equal(4, panel.LoadCount);
            Assert.Equal(4, Pack(new List<PanelResult> { panel }).Links[0].Loads);
        }

        /// <summary>Keypads are one device each and place independently, so they fill the gaps whole
        /// panels leave rather than forcing a link of their own. 1 panel device + 98 keypads = 99,
        /// exactly one link.</summary>
        [Fact]
        public void KeypadsFillGapsRatherThanForcingLinks()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };

            Assert.Equal(1, Pack(panels, new BomExtras { KeypadCount = 98 }).QsLinkCount);
            Assert.Equal(2, Pack(panels, new BomExtras { KeypadCount = 99 }).QsLinkCount);
        }

        /// <summary>Two-gang keypads are two devices.</summary>
        [Fact]
        public void TwoGangKeypadsCountTwice()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            Assert.Equal(1, Pack(panels, new BomExtras { TwoGangKeypadCount = 49 }).QsLinkCount);
            Assert.Equal(2, Pack(panels, new BomExtras { TwoGangKeypadCount = 50 }).QsLinkCount);
        }
    }

    /// <summary>Clear Connect Type A: wireless takes whole links off a processor.</summary>
    public class ClearConnectLinkTests : ControlLinkPackerTestBase
    {
        /// <summary>Lutron 369-351b p.1: "Up to four (4) total Hybrid Repeaters can be used per link."
        /// This was the QS device cap of 99 until the spec was read, which meant any job up to 99
        /// repeaters reported a single Clear Connect link.</summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(4, 1)]
        [InlineData(5, 2)]   // 99 would still have said 1
        [InlineData(8, 2)]
        [InlineData(9, 3)]
        public void RepeatersCapAtFourPerLink(int repeaters, int expectedLinks)
        {
            var packed = Pack(new List<PanelResult> { Panel("1-A", modules: 1) },
                new BomExtras { HybridRepeaters = Tally.Repeaters(repeaters) });

            Assert.Equal(expectedLinks, packed.ClearConnectLinkCount);
        }

        /// <summary>The consequence that matters on a purchasing document: a job that runs on one
        /// processor needs two the moment wireless outgrows a single link. 1 QS + 2 CC-A = 3 links.</summary>
        [Fact]
        public void WirelessAloneCanForceASecondProcessor()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };

            Assert.Equal(1, Recommend(panels, new BomExtras { HybridRepeaters = Tally.Repeaters(4) }));
            Assert.Equal(2, Recommend(panels, new BomExtras { HybridRepeaters = Tally.Repeaters(5) }));
        }

        /// <summary>A Clear Connect link never shares QS work — the panels stay on the wired link.</summary>
        [Fact]
        public void ClearConnectTakesNoQsWork()
        {
            var packed = Pack(new List<PanelResult> { Panel("1-A", modules: 3) },
                new BomExtras { HybridRepeaters = Tally.Repeaters(2) });

            var cca = packed.Links.Single(l => l.IsClearConnect);
            Assert.Equal(2, cca.Devices);
            Assert.Equal(0, cca.Loads);
            Assert.False(cca.IsOverCapacity);
        }

        /// <summary>
        /// A Clear Connect link carries switch legs too — 100 of them, a fifth of the wired link's 512
        /// (Lutron 3691127f p.2). This read 0 until the processor sheet was checked, which would have
        /// let any wireless output overflow the link with nothing said.
        ///
        /// Nothing fills it today: a wireless keypad is a control, not an output, and wireless dimmers,
        /// shades and Sivoia drives are not collected. The capacity is declared so it is right the
        /// moment one of them is.
        /// </summary>
        [Fact]
        public void ClearConnectHasItsOwnLoadCap()
        {
            var packed = Pack(new List<PanelResult> { Panel("1-A", modules: 3) },
                new BomExtras { HybridRepeaters = Tally.Repeaters(2) });

            var cca = packed.Links.Single(l => l.IsClearConnect);
            var qs = packed.Links.First(l => !l.IsClearConnect);

            Assert.Equal(ProcessorLink.MaxClearConnectLoads, cca.LoadCapacity);
            Assert.Equal(100, cca.LoadCapacity);
            Assert.Equal(512, qs.LoadCapacity);
        }

        /// <summary>
        /// The four-repeater cap is a cap on REPEATERS, not on devices. A Clear Connect link is still
        /// a 99-device link — a wireless keypad consumes its device budget exactly as a wired one
        /// consumes a QS link's, and can run well past four.
        ///
        /// Reading the two as one number is the mistake this pins: it would make every wireless device
        /// past the fourth look like an overflow, on a link with 95 slots left.
        /// </summary>
        [Fact]
        public void RepeaterCapIsNotADeviceCap()
        {
            var cca = Pack(new List<PanelResult> { Panel("1-A", modules: 3) },
                new BomExtras { HybridRepeaters = Tally.Repeaters(4) }).Links.Single(l => l.IsClearConnect);

            Assert.Equal(ProcessorLink.MaxDevices, cca.DeviceCapacity);   // 99, not 4
            Assert.Equal(4, cca.RepeaterCapacity);
            Assert.Equal(4, cca.Repeaters);
            Assert.Equal(4, cca.Devices);                                 // repeaters ARE devices
            Assert.False(cca.IsOverCapacity);
        }

        /// <summary>
        /// Wireless devices ride the Clear Connect link and consume its 99-device budget, so the
        /// repeater cap and the device cap can each bind independently. Four repeaters serving 90
        /// keypads is one link on both counts; push the keypads past 99 and a second link is needed
        /// even though the repeaters still fit comfortably.
        /// </summary>
        [Fact]
        public void WirelessDeviceCountCanDriveTheLinkCountOnItsOwn()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };

            int Links(int wireless) => Pack(panels,
                new BomExtras { HybridRepeaters = Tally.Repeaters(4), WirelessDeviceCount = wireless })
                .ClearConnectLinkCount;

            Assert.Equal(1, Links(90));    // 4 + 90 = 94 devices, 4 repeaters — one link on both caps
            Assert.Equal(2, Links(96));    // 4 + 96 = 100 devices — over 99, second link, repeaters unchanged
        }

        /// <summary>A wireless keypad is not a QS device. Counting it as one charges a link that never
        /// sees it and leaves the link that does under-reported.</summary>
        [Fact]
        public void WirelessKeypadsDoNotLandOnTheQsLink()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var packed = Pack(panels,
                new BomExtras { HybridRepeaters = Tally.Repeaters(1), WirelessDeviceCount = 40 });

            Assert.Equal(1, packed.Links[0].Devices);           // the panel's module, and nothing else
            Assert.Equal(41, packed.Links[1].Devices);          // 1 repeater + 40 keypads
            Assert.Equal(1, packed.Links[1].Repeaters);
        }

        /// <summary>Wireless devices with no repeater modelled still get a link — they have to live
        /// somewhere — and the repeater bar reading 0 of 4 is how the missing repeater shows up.</summary>
        [Fact]
        public void WirelessWithNoRepeaterStillGetsALinkAndShowsTheGap()
        {
            var packed = Pack(new List<PanelResult> { Panel("1-A", modules: 1) },
                new BomExtras { WirelessDeviceCount = 12 });

            var cca = packed.Links.Single(l => l.IsClearConnect);
            Assert.Equal(12, cca.Devices);
            Assert.Equal(0, cca.Repeaters);
            Assert.False(cca.IsOverCapacity);
        }

        /// <summary>Clear Connect links come LAST, so a caller mapping them positionally onto Link 1,
        /// Link 2… gets "the trailing links go wireless" for free.</summary>
        [Fact]
        public void ClearConnectLinksSortAfterQsLinks()
        {
            var packed = Pack(new List<PanelResult> { Panel("1-A", modules: 3) },
                new BomExtras { HybridRepeaters = Tally.Repeaters(1) }, availableLinks: 2);

            Assert.Equal(ProcessorLink.QsLinkType, packed.Links[0].LinkType);
            Assert.Equal(ProcessorLink.ClearConnectLinkType, packed.Links[1].LinkType);
        }

        /// <summary>With a fixed link budget, wireless stops one short of taking every link that has QS
        /// work to do. Five repeaters want two CC-A links but only one processor is sited: rather than
        /// leaving the panels with nowhere to go, the overflow shows as an over-capacity CC-A bar —
        /// which is the same "out of links" message, said where the designer is looking. The
        /// unconstrained recommendation independently says 2 processors.</summary>
        [Fact]
        public void FixedBudgetShowsRepeaterOverflowRatherThanHidingThePanels()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 3) };
            var extras = new BomExtras { HybridRepeaters = Tally.Repeaters(5) };

            var packed = Pack(panels, extras, availableLinks: 2);

            Assert.Equal(1, packed.QsLinkCount);
            Assert.Equal(3, packed.Links[0].Devices);          // the panel still has a home
            Assert.Equal(5, packed.Links[1].Devices);          // all five repeaters, on one CC-A link
            Assert.True(packed.Links[1].IsOverCapacity);       // 5 > 4
            Assert.Equal(2, Recommend(panels, extras));
        }
    }

    /// <summary>Subsystem demand (DMX today, DALI later): sited where the designer sited it, floating
    /// where they have not.</summary>
    public class SubsystemDemandTests : ControlLinkPackerTestBase
    {
        /// <summary>The shipped bug this seam closes: a DMX interface used to count as +1 device and
        /// ZERO loads, so every switch leg it drove was invisible to the link display. Each DMX
        /// channel is a switch leg.</summary>
        [Fact]
        public void DmxChannelsReachTheLoadBudget()
        {
            var panels = new List<PanelResult> { WithCompartment(Panel("1-A", modules: 1), "DMX") };
            var extras = new BomExtras { SubsystemDemands = new[] { DmxDemand(interfaces: 1, channels: 32) } };

            var link = Pack(panels, extras).Links.Single();
            Assert.Equal(2, link.Devices);          // 1 module + 1 interface
            Assert.Equal(36, link.Loads);           // 4 module outputs + 32 DMX channels
        }

        /// <summary>Budgets split evenly across the required interfaces, largest remainder, so sited
        /// and floating shares always add back to the job total.</summary>
        [Fact]
        public void InterfaceBudgetsSplitAcrossTheRequirement()
        {
            var panels = new List<PanelResult> { WithCompartment(Panel("1-A", modules: 0), "DMX") };
            var extras = new BomExtras { SubsystemDemands = new[] { DmxDemand(interfaces: 2, channels: 50) } };

            var demand = ControlLinkPacker.BuildDemand(panels, extras);

            // 50 legs over 2 interfaces = 25 + 25; the sited one takes the first share.
            Assert.Equal(25, demand.PinnedUnits.Single().Loads);
            Assert.Equal(25, demand.FloatingUnits.Single().Loads);
            Assert.Equal(1, demand.FloatingUnits.Single().Devices);
        }

        /// <summary>An interface the solve requires but nobody has sited still consumes link capacity —
        /// the job needs it whether or not a compartment has been picked for it yet. Otherwise the
        /// bars read comfortable on a design that does not fit.</summary>
        [Fact]
        public void UnsitedInterfacesStillConsumeCapacity()
        {
            var panels = new List<PanelResult> { Panel("1-A", modules: 1) };
            var extras = new BomExtras { SubsystemDemands = new[] { DmxDemand(interfaces: 16, channels: 512) } };

            var packed = Pack(panels, extras);

            // 512 DMX legs alone fill a link; the panel's 4 push it over into a second.
            Assert.Equal(2, packed.QsLinkCount);
            Assert.Equal(1, Recommend(panels, extras));
        }

        /// <summary>Siting MORE interfaces than the solve asked for is honored — they are real devices
        /// on the link — but they bring no legs of their own.</summary>
        [Fact]
        public void InterfacesSitedBeyondTheRequirementStillCountAsDevices()
        {
            var panels = new List<PanelResult>
            {
                WithCompartment(Panel("1-A", modules: 0), "DMX"),
                WithCompartment(Panel("2-A", modules: 0), "DMX")
            };
            var extras = new BomExtras { SubsystemDemands = new[] { DmxDemand(interfaces: 1, channels: 32) } };

            var link = Pack(panels, extras).Links.Single();
            Assert.Equal(2, link.Devices);
            Assert.Equal(32, link.Loads);
        }

        /// <summary>A compartment device nobody speaks for — a QSE-IO, or a QSE-CI-DMX on a job where
        /// TurboDMX has nothing to say — is one QS device and no legs.</summary>
        [Fact]
        public void UnclaimedCompartmentDeviceIsOneDevice()
        {
            var panels = new List<PanelResult> { WithCompartment(Panel("1-A", modules: 1), "Digital I/O") };
            var link = Pack(panels).Links.Single();

            Assert.Equal(2, link.Devices);
            Assert.Equal(4, link.Loads);
        }

        /// <summary>A subsystem's interface is counted once, not twice — from its demand, not from the
        /// compartment pick as well.</summary>
        [Fact]
        public void SitedInterfaceIsNotDoubleCounted()
        {
            var panels = new List<PanelResult> { WithCompartment(Panel("1-A", modules: 0), "DMX") };
            var extras = new BomExtras { SubsystemDemands = new[] { DmxDemand(interfaces: 1, channels: 10) } };

            Assert.Equal(1, Pack(panels, extras).Links.Single().Devices);
        }

        /// <summary>The processor is the head end of its own links, not a device on them.</summary>
        [Fact]
        public void ProcessorSelectionIsNotADeviceOnItsOwnLink()
        {
            var panels = new List<PanelResult> { WithCompartment(Panel("1-A", modules: 1), "Processor") };
            Assert.Equal(1, Pack(panels).Links.Single().Devices);
        }

        /// <summary>A demand with no compartment part — a future DALI DIN module — has nothing to pin
        /// it to, so its budget pours like keypads rather than packing as one indivisible lump.</summary>
        [Fact]
        public void DemandWithNoCompartmentPartPours()
        {
            var extras = new BomExtras
            {
                SubsystemDemands = new[]
                {
                    new ControlSubsystemDemand(
                        "DALI",
                        new List<DemandPart> { new DemandPart("LQSE-DALI", 4, DemandMount.DinSlot) },
                        linkDevices: 4,
                        linkLoads: 200)
                }
            };

            var packed = Pack(new List<PanelResult>(), extras);
            Assert.Equal(1, packed.QsLinkCount);
            Assert.Equal(4, packed.Links[0].Devices);
            Assert.Equal(200, packed.Links[0].Loads);
        }
    }

    /// <summary>The two questions, and the invariant that binds them.</summary>
    public class RecommendationInvariantTests : ControlLinkPackerTestBase
    {
        /// <summary>A job with nothing in it is still a system, and a system has a processor.</summary>
        [Fact]
        public void EmptyJobStillNeedsOneProcessor()
        {
            Assert.Equal(1, Recommend(new List<PanelResult>()));
            Assert.Equal(1, ControlLinkPacker.RecommendProcessors(null));
        }

        /// <summary>The invariant, stated directly: pack the job into the links the RECOMMENDED number
        /// of processors would provide, and nothing is over capacity. If this fails, the bars and the
        /// BOM are telling the designer different things.</summary>
        [Theory]
        [InlineData(33, 6, 0, 0)]
        [InlineData(1, 1, 0, 5)]
        [InlineData(4, 9, 300, 0)]
        [InlineData(12, 8, 40, 3)]
        public void RecommendedProcessorsAlwaysFitTheJob(
            int panelCount, int modulesEach, int keypads, int repeaters)
        {
            var panels = Enumerable.Range(1, panelCount)
                .Select(i => Panel($"P{i:00}", modules: modulesEach)).ToList();
            var extras = new BomExtras { KeypadCount = keypads, HybridRepeaters = Tally.Repeaters(repeaters) };

            int processors = Recommend(panels, extras);
            var packed = Pack(panels, extras, availableLinks: processors * ControlLinkPacker.LinksPerProcessor);

            Assert.All(packed.Links, link => Assert.False(link.IsOverCapacity));
        }

        /// <summary>And the other direction: one processor fewer than recommended always shows the
        /// designer an over-capacity bar. A recommendation nobody can see is not a recommendation.</summary>
        [Theory]
        [InlineData(33, 6, 0, 0)]
        [InlineData(1, 1, 0, 5)]
        [InlineData(12, 8, 40, 3)]
        public void OneProcessorShortAlwaysShowsAnOverCapacityBar(
            int panelCount, int modulesEach, int keypads, int repeaters)
        {
            var panels = Enumerable.Range(1, panelCount)
                .Select(i => Panel($"P{i:00}", modules: modulesEach)).ToList();
            var extras = new BomExtras { KeypadCount = keypads, HybridRepeaters = Tally.Repeaters(repeaters) };

            int processors = Recommend(panels, extras);
            Assert.True(processors > 1, "fixture must need more than one processor to test a shortfall");

            var packed = Pack(panels, extras,
                availableLinks: (processors - 1) * ControlLinkPacker.LinksPerProcessor);

            Assert.Contains(packed.Links, link => link.IsOverCapacity);
        }

        /// <summary>
        /// A unit too big for any link gets its own link and shows as over capacity — the one shape
        /// where more processors is NOT the answer, so the recommendation deliberately does not climb
        /// chasing it.
        ///
        /// Not reachable from real hardware: a Lutron panel tops out at 9 modules × 4 outputs = 36
        /// legs, and a QSE-CI-DMX at 32 channels, both far inside a link. It is pinned because the
        /// packer must stay honest on input no product can produce — an earlier BOM fixture asserted
        /// against "one interface carrying 1000 channels", and the pooled arithmetic it was written
        /// for happily divided that across links it could never occupy.
        /// </summary>
        [Fact]
        public void AUnitLargerThanALinkTakesItsOwnLinkAndShowsOver()
        {
            var oversized = Panel("1-A", modules: 200, cap: 4);   // 200 devices, 800 legs
            var packed = Pack(new List<PanelResult> { oversized });

            Assert.Equal(1, packed.QsLinkCount);
            Assert.True(packed.Links[0].IsOverCapacity);
            Assert.Equal(1, Recommend(new List<PanelResult> { oversized }));
        }

        /// <summary>Largest remainder: shares sum back to the total exactly, leftovers land first.</summary>
        [Fact]
        public void SplitDistributesRemainderToTheFirstShares()
        {
            Assert.Equal(new[] { 3, 3, 2, 2 }, ControlLinkPacker.Split(10, 4));
            Assert.Equal(new[] { 0, 0 }, ControlLinkPacker.Split(0, 2));
            Assert.Empty(ControlLinkPacker.Split(10, 0));
        }

        /// <summary>Packing is deterministic — the same job packs the same way every rebuild, so the
        /// bars do not shuffle while the designer is looking at them.</summary>
        [Fact]
        public void PackingIsStableAcrossRuns()
        {
            var panels = Enumerable.Range(1, 20).Select(i => Panel($"P{i:00}", modules: i % 9 + 1)).ToList();
            var extras = new BomExtras { KeypadCount = 30 };

            var first = Pack(panels, extras).Links.Select(l => (l.Devices, l.Loads)).ToList();
            var second = Pack(panels, extras).Links.Select(l => (l.Devices, l.Loads)).ToList();

            Assert.Equal(first, second);
        }
    }
}
