using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for LinkAssignmentService (Core/Zones/Services/LinkAssignmentService.cs).
    //
    //  The service used to BE the link math. It is now an adapter: it builds one ProcessorInstance per
    //  placed "Processor" compartment slot, runs ControlLinkPacker against the links those processors
    //  provide, and writes the result onto each instance's Link1/Link2. So the packing rules belong in
    //  ControlLinkPackerTests — what belongs HERE is the adapting: which slots spawn processors, the
    //  positional mapping (Clear Connect lands on the trailing links), and that a link's type is set
    //  before the flags that depend on its capacity.
    //
    //  For me (Claude): a processor is a "Processor" COMPARTMENT SELECTION, not a flag — per slot, so an
    //  LV21 with a processor in each of its two compartments is two instances and four link bars. That
    //  is the same count the BOM's supply sizer uses. Panels need a special compartment (sizes 0/4/8)
    //  for a selection to register; the LV21 (size 0) is the dual-compartment one. Returns the
    //  instances; assert on those.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class LinkAssignmentTests
    {
        /// <summary>A single-compartment processor panel (PD8) carrying <paramref name="modules"/>
        /// modules of capacity <paramref name="cap"/> → DeviceCount=modules, LoadCount=modules*cap.</summary>
        private static PanelResult Proc(string name, int modules, int cap)
        {
            var p = new PanelResult
            {
                PanelName = name,
                SelectedPanelSize = 8,
                SpecialCompartmentPanelSizes = new HashSet<int> { 0, 4, 8 },
                SelectedSpecialDevice = "Processor"
            };
            for (int i = 0; i < modules; i++)
                p.Modules.Add(new ModuleResult { ModuleCapacity = cap });
            return p;
        }

        /// <summary>An LV21 (dual-compartment) carrying whatever the two compartments hold.</summary>
        private static PanelResult Lv21(string name, string slot1, string slot2)
            => new PanelResult
            {
                PanelName = name,
                SelectedPanelSize = 0,
                SpecialCompartmentPanelSizes = new HashSet<int> { 0, 4, 8 },
                DualCompartmentPanelSizes = new HashSet<int> { 0 },
                SelectedSpecialDevice = slot1,
                SelectedSpecialDevice2 = slot2
            };

        // Production always passes the brand (the VM hands the packer _currentBrand), so the bars see
        // a compartment device's nameplate switch legs. Default to Lutron; the one null-brand case
        // below passes it explicitly to pin that a brand with no leg model contributes 0.
        private static List<ProcessorInstance> Assign(
            List<PanelResult> panels, BomExtras? extras = null, BrandConfig? brand = null)
            => LinkAssignmentService.BuildProcessorInstances(
                panels, extras ?? new BomExtras(), brand ?? BrandConfig.Lutron);

        [Fact]
        public void NoProcessorPanels_NoInstances()
        {
            var plain = new PanelResult { PanelName = "1-A" }; // no compartment selection
            Assert.Empty(Assign(new List<PanelResult> { plain }));
        }

        [Fact]
        public void EmptyPanelList_NoThrow() => Assert.Empty(Assign(new List<PanelResult>()));

        [Fact]
        public void NullPanelList_NoThrow()
            => Assert.Empty(LinkAssignmentService.BuildProcessorInstances(null!, new BomExtras()));

        [Fact]
        public void SingleProcessor_GetsTwoQsLinks_AndPacksOntoTheFirst()
        {
            var proc = Proc("1-A", modules: 3, cap: 4); // DeviceCount=3, LoadCount=12
            var inst = Assert.Single(Assign(new List<PanelResult> { proc }));

            Assert.Equal(ProcessorLink.QsLinkType, inst.Link1.LinkType);
            Assert.Equal(ProcessorLink.QsLinkType, inst.Link2.LinkType);
            Assert.Equal(3, inst.Link1.UsedDevices);
            Assert.Equal(12, inst.Link1.UsedLoads);
            Assert.Equal(0, inst.Link2.UsedDevices);
        }

        /// <summary>Two processor panels, four links, in panel order.</summary>
        [Fact]
        public void EachProcessorContributesTwoLinks()
        {
            var a = Proc("1-A", modules: 2, cap: 4);
            var b = Proc("2-A", modules: 2, cap: 4);
            var insts = Assign(new List<PanelResult> { a, b });

            Assert.Equal(2, insts.Count);
            Assert.Equal(1, insts[0].Link1.LinkNumber);
            Assert.Equal(2, insts[0].Link2.LinkNumber);
            Assert.Equal("1-A", insts[0].Link1.ProcessorPanelName);
            Assert.Equal("2-A", insts[1].Link1.ProcessorPanelName);
        }

        /// <summary>Two processors in one LV21's two compartments are two instances and four links —
        /// the display gap the per-slot model closes. Labels distinguish them; the panel is shared.</summary>
        [Fact]
        public void DualProcessorLv21_YieldsTwoInstances_FourLinks()
        {
            var insts = Assign(new List<PanelResult> { Lv21("1-A", "Processor", "Processor") });

            Assert.Equal(2, insts.Count);
            Assert.All(insts, i => Assert.Equal("1-A", i.PanelName));
            Assert.Equal("1-A (1)", insts[0].Label);
            Assert.Equal("1-A (2)", insts[1].Label);
            Assert.All(insts, i =>
            {
                Assert.Equal(ProcessorLink.QsLinkType, i.Link1.LinkType);
                Assert.Equal(ProcessorLink.QsLinkType, i.Link2.LinkType);
            });
        }

        /// <summary>A lone processor's instance is labelled by the bare panel name — no "(1)" suffix
        /// until a panel actually holds more than one.</summary>
        [Fact]
        public void SingleProcessorInstance_LabelledByPanelName()
            => Assert.Equal("1-A", Assert.Single(Assign(new List<PanelResult> { Proc("1-A", 1, 4) })).Label);

        /// <summary>Keypads pour into whatever room the panels left, filling links in order rather
        /// than spreading — packing tightly is the point when the question is "how many links".</summary>
        [Fact]
        public void KeypadsFillTheFirstLinkBeforeTheSecond()
        {
            var proc = Proc("1-A", modules: 3, cap: 4);
            var inst = Assert.Single(Assign(new List<PanelResult> { proc }, new BomExtras { KeypadCount = 10 }));

            Assert.Equal(13, inst.Link1.UsedDevices);   // 3 modules + 10 keypads, room for 96 more
            Assert.Equal(0, inst.Link2.UsedDevices);
        }

        /// <summary>Wireless takes the TRAILING link, which is what the packer's ordering guarantees:
        /// the panel keeps Link 1 and the repeaters get Link 2.</summary>
        [Fact]
        public void WirelessDevices_ReserveTheLastLinkAsClearConnect()
        {
            var proc = Proc("1-A", modules: 2, cap: 4); // DeviceCount=2, LoadCount=8
            var inst = Assert.Single(
                Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(3) }));

            Assert.Equal(ProcessorLink.ClearConnectLinkType, inst.Link2.LinkType);
            Assert.Equal(3, inst.Link2.UsedDevices);
            Assert.Equal(0, inst.Link2.UsedLoads);
            Assert.Equal(ProcessorLink.QsLinkType, inst.Link1.LinkType);
            Assert.Equal(2, inst.Link1.UsedDevices);
            Assert.Equal(8, inst.Link1.UsedLoads);
        }

        /// <summary>Overflow shows on the REPEATER bar, not the device bar. Five repeaters is over the
        /// cap of four, but five devices is nowhere near a link's 99 — a Clear Connect link is a
        /// 99-device link that caps one kind of device at four, and the two bars say different
        /// things.</summary>
        [Fact]
        public void ClearConnectOverflowShowsOnTheRepeaterBarNotTheDeviceBar()
        {
            var proc = Proc("1-A", modules: 2, cap: 4);
            var inst = Assert.Single(
                Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(5) }));

            Assert.Equal(ProcessorLink.MaxDevices, inst.Link2.DeviceCapacity);
            Assert.Equal(5, inst.Link2.UsedDevices);
            Assert.False(inst.Link2.IsOverDeviceCapacity);   // 5 of 99

            Assert.Equal(5, inst.Link2.UsedRepeaters);
            Assert.Equal(4, inst.Link2.RepeaterCapacity);
            Assert.True(inst.Link2.IsOverRepeaterCapacity);  // 5 of 4 — this is the signal

            Assert.True(inst.Link2.ShowRepeaterBar);
        }

        /// <summary>Clear Connect shows three bars where QS shows two — devices and switch legs on both,
        /// repeaters only where they exist. The leg cap differs by link type: 100 against the wired
        /// link's 512 (Lutron 3691127f p.2).</summary>
        [Fact]
        public void ClearConnectShowsThreeBudgetsAndQsShowsTwo()
        {
            var proc = Proc("1-A", modules: 2, cap: 4);
            var inst = Assert.Single(
                Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(2) }));

            Assert.False(inst.Link1.ShowRepeaterBar);
            Assert.Equal(512, inst.Link1.LoadCapacity);

            Assert.True(inst.Link2.ShowRepeaterBar);
            Assert.Equal(100, inst.Link2.LoadCapacity);
            Assert.Equal(99, inst.Link2.DeviceCapacity);
            Assert.Equal(4, inst.Link2.RepeaterCapacity);
        }

        /// <summary>A QSE-IO in the processor's second LV21 compartment counts as one extra device AND
        /// its five nameplate switch legs on its link — the compartment device rides the panel it sits
        /// in, and its outputs are real load-bar demand.</summary>
        [Fact]
        public void SpecialCompartmentDigitalIO_AddsOneDeviceAndFiveLegs()
        {
            var proc = Lv21("1-A", "Processor", "Digital I/O"); // QSE-IO shares the processor's panel
            proc.Modules.Add(new ModuleResult { ModuleCapacity = 4 }); // DeviceCount=1

            var inst = Assert.Single(Assign(new List<PanelResult> { proc }));

            Assert.Equal(2, inst.Link1.UsedDevices);   // 1 module + 1 QSE-IO
            Assert.Equal(9, inst.Link1.UsedLoads);     // 4 module outputs + 5 QSE-IO legs
        }

        /// <summary>The legs are a brand fact, so a null brand (or one with no leg model) contributes
        /// none — the same QSE-IO shows its device but no legs. This is what the pre-leg bars did, and
        /// the gate that keeps a leg-less brand honest.</summary>
        [Fact]
        public void NullBrand_ContributesNoSwitchLegs()
        {
            var proc = Lv21("1-A", "Processor", "Digital I/O");
            proc.Modules.Add(new ModuleResult { ModuleCapacity = 4 });

            var inst = Assert.Single(LinkAssignmentService.BuildProcessorInstances(
                new List<PanelResult> { proc }, new BomExtras(), brand: null));

            Assert.Equal(2, inst.Link1.UsedDevices);   // device still counts
            Assert.Equal(4, inst.Link1.UsedLoads);     // but no legs without a brand
        }

        /// <summary>An "Empty" second compartment adds nothing — only a device selection increments.</summary>
        [Fact]
        public void NonSpecialDeviceSelection_NoExtraDevice()
        {
            var proc = Lv21("1-A", "Processor", "Empty");
            proc.Modules.Add(new ModuleResult { ModuleCapacity = 4 });

            var inst = Assert.Single(Assign(new List<PanelResult> { proc }));

            Assert.Equal(1, inst.Link1.UsedDevices);
        }

        /// <summary>The bug this seam closes, seen from the display: a sited DMX interface used to add
        /// one device and no loads, so its switch legs never moved the load bar at all. Here the DMX
        /// panel is separate from the processor, and its interface packs onto the processor's link.</summary>
        [Fact]
        public void DmxChannelsMoveTheLoadBar()
        {
            var proc = Proc("1-A", modules: 1, cap: 4);
            var dmxPanel = new PanelResult
            {
                PanelName = "2-A",
                SpecialCompartmentPanelSizes = new HashSet<int> { 4 },
                SelectedPanelSize = 4,
                SelectedSpecialDevice = "DMX"
            };

            var inst = Assert.Single(Assign(new List<PanelResult> { proc, dmxPanel }, new BomExtras
            {
                SubsystemDemands = new[]
                {
                    new ControlSubsystemDemand(
                        "DMX",
                        new List<DemandPart> { new DemandPart("QSE-CI-DMX", 1, DemandMount.LvCompartment) },
                        linkDevices: 1,
                        linkLoads: 64)
                }
            }));

            Assert.Equal(2, inst.Link1.UsedDevices);   // 1 module + 1 interface
            Assert.Equal(68, inst.Link1.UsedLoads);    // 4 module outputs + 64 DMX channels
        }
    }
}
