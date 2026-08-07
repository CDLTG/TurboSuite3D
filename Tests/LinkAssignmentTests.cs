using System.Collections.Generic;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for LinkAssignmentService (Core/Zones/Services/LinkAssignmentService.cs).
    //
    //  The service used to BE the link math. It is now an adapter: it runs ControlLinkPacker against
    //  the links the sited processors provide and writes the result onto PanelResult.Link1/Link2.
    //  So the packing rules belong in ControlLinkPackerTests — what belongs HERE is the adapting:
    //  which panels spawn links, the positional mapping (Clear Connect lands on the trailing links),
    //  and that a link's type is set before the flags that depend on its capacity.
    //
    //  For me (Claude): IsProcessor is what makes a panel spawn links — the real ViewModel sets it
    //  from the compartment selection, and a panel can be a processor here without one. Mutates
    //  Link1/Link2 in place; assert on those after the call. Derivations inline.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class LinkAssignmentTests
    {
        /// <summary>A processor panel with <paramref name="modules"/> modules of capacity
        /// <paramref name="cap"/> → DeviceCount=modules, LoadCount=modules*cap.</summary>
        private static PanelResult Proc(string name, int modules, int cap)
        {
            var p = new PanelResult { PanelName = name, IsProcessor = true };
            for (int i = 0; i < modules; i++)
                p.Modules.Add(new ModuleResult { ModuleCapacity = cap });
            return p;
        }

        private static void Assign(List<PanelResult> panels, BomExtras? extras = null)
            => LinkAssignmentService.AssignAndAggregate(panels, extras ?? new BomExtras());

        [Fact]
        public void NoProcessorPanels_NoLinksCreated()
        {
            var plain = new PanelResult { PanelName = "1-A" }; // IsProcessor false
            Assign(new List<PanelResult> { plain });
            Assert.Null(plain.Link1);
            Assert.Null(plain.Link2);
        }

        [Fact]
        public void EmptyPanelList_NoThrow() => Assign(new List<PanelResult>());

        [Fact]
        public void NullPanelList_NoThrow()
            => LinkAssignmentService.AssignAndAggregate(null!, new BomExtras());

        [Fact]
        public void SingleProcessor_GetsTwoQsLinks_AndPacksOntoTheFirst()
        {
            var proc = Proc("1-A", modules: 3, cap: 4); // DeviceCount=3, LoadCount=12
            Assign(new List<PanelResult> { proc });

            Assert.Equal(ProcessorLink.QsLinkType, proc.Link1.LinkType);
            Assert.Equal(ProcessorLink.QsLinkType, proc.Link2.LinkType);
            Assert.Equal(3, proc.Link1.UsedDevices);
            Assert.Equal(12, proc.Link1.UsedLoads);
            Assert.Equal(0, proc.Link2.UsedDevices);
        }

        /// <summary>Two processors, four links, in panel order.</summary>
        [Fact]
        public void EachProcessorContributesTwoLinks()
        {
            var a = Proc("1-A", modules: 2, cap: 4);
            var b = Proc("2-A", modules: 2, cap: 4);
            Assign(new List<PanelResult> { a, b });

            Assert.Equal(1, a.Link1.LinkNumber);
            Assert.Equal(2, a.Link2.LinkNumber);
            Assert.Equal("1-A", a.Link1.ProcessorPanelName);
            Assert.Equal("2-A", b.Link1.ProcessorPanelName);
        }

        /// <summary>Keypads pour into whatever room the panels left, filling links in order rather
        /// than spreading — packing tightly is the point when the question is "how many links".</summary>
        [Fact]
        public void KeypadsFillTheFirstLinkBeforeTheSecond()
        {
            var proc = Proc("1-A", modules: 3, cap: 4);
            Assign(new List<PanelResult> { proc }, new BomExtras { KeypadCount = 10 });

            Assert.Equal(13, proc.Link1.UsedDevices);   // 3 modules + 10 keypads, room for 96 more
            Assert.Equal(0, proc.Link2.UsedDevices);
        }

        /// <summary>Wireless takes the TRAILING link, which is what the packer's ordering guarantees:
        /// the panel keeps Link 1 and the repeaters get Link 2.</summary>
        [Fact]
        public void WirelessDevices_ReserveTheLastLinkAsClearConnect()
        {
            var proc = Proc("1-A", modules: 2, cap: 4); // DeviceCount=2, LoadCount=8
            Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(3) });

            Assert.Equal(ProcessorLink.ClearConnectLinkType, proc.Link2.LinkType);
            Assert.Equal(3, proc.Link2.UsedDevices);
            Assert.Equal(0, proc.Link2.UsedLoads);
            Assert.Equal(ProcessorLink.QsLinkType, proc.Link1.LinkType);
            Assert.Equal(2, proc.Link1.UsedDevices);
            Assert.Equal(8, proc.Link1.UsedLoads);
        }

        /// <summary>Overflow shows on the REPEATER bar, not the device bar. Five repeaters is over the
        /// cap of four, but five devices is nowhere near a link's 99 — a Clear Connect link is a
        /// 99-device link that caps one kind of device at four, and the two bars say different
        /// things.</summary>
        [Fact]
        public void ClearConnectOverflowShowsOnTheRepeaterBarNotTheDeviceBar()
        {
            var proc = Proc("1-A", modules: 2, cap: 4);
            Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(5) });

            Assert.Equal(ProcessorLink.MaxDevices, proc.Link2.DeviceCapacity);
            Assert.Equal(5, proc.Link2.UsedDevices);
            Assert.False(proc.Link2.IsOverDeviceCapacity);   // 5 of 99

            Assert.Equal(5, proc.Link2.UsedRepeaters);
            Assert.Equal(4, proc.Link2.RepeaterCapacity);
            Assert.True(proc.Link2.IsOverRepeaterCapacity);  // 5 of 4 — this is the signal

            Assert.True(proc.Link2.ShowRepeaterBar);
        }

        /// <summary>Clear Connect shows three bars where QS shows two — devices and switch legs on both,
        /// repeaters only where they exist. The leg cap differs by link type: 100 against the wired
        /// link's 512 (Lutron 3691127f p.2).</summary>
        [Fact]
        public void ClearConnectShowsThreeBudgetsAndQsShowsTwo()
        {
            var proc = Proc("1-A", modules: 2, cap: 4);
            Assign(new List<PanelResult> { proc }, new BomExtras { HybridRepeaters = Tally.Repeaters(2) });

            Assert.False(proc.Link1.ShowRepeaterBar);
            Assert.Equal(512, proc.Link1.LoadCapacity);

            Assert.True(proc.Link2.ShowRepeaterBar);
            Assert.Equal(100, proc.Link2.LoadCapacity);
            Assert.Equal(99, proc.Link2.DeviceCapacity);
            Assert.Equal(4, proc.Link2.RepeaterCapacity);
        }

        [Fact]
        public void SpecialCompartmentDigitalIO_CountsAsOneExtraDevice()
        {
            var proc = Proc("1-A", modules: 1, cap: 4); // DeviceCount=1
            proc.SpecialCompartmentPanelSizes = new HashSet<int> { 4 };
            proc.SelectedPanelSize = 4;                 // → HasSpecialCompartment true
            proc.SelectedSpecialDevice = "Digital I/O"; // QSE-IO counts as 1 device on its link

            Assign(new List<PanelResult> { proc });

            Assert.Equal(2, proc.Link1.UsedDevices);
            Assert.Equal(4, proc.Link1.UsedLoads);
        }

        [Fact]
        public void NonSpecialDeviceSelection_NoExtraDevice()
        {
            var proc = Proc("1-A", modules: 1, cap: 4);
            proc.SpecialCompartmentPanelSizes = new HashSet<int> { 4 };
            proc.SelectedPanelSize = 4;
            proc.SelectedSpecialDevice = "Empty"; // not a device → no increment

            Assign(new List<PanelResult> { proc });

            Assert.Equal(1, proc.Link1.UsedDevices);
        }

        /// <summary>The bug this seam closes, seen from the display: a sited DMX interface used to add
        /// one device and no loads, so its switch legs never moved the load bar at all.</summary>
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

            Assign(new List<PanelResult> { proc, dmxPanel }, new BomExtras
            {
                SubsystemDemands = new[]
                {
                    new ControlSubsystemDemand(
                        "DMX",
                        new List<DemandPart> { new DemandPart("QSE-CI-DMX", 1, DemandMount.LvCompartment) },
                        linkDevices: 1,
                        linkLoads: 64)
                }
            });

            Assert.Equal(2, proc.Link1.UsedDevices);   // 1 module + 1 interface
            Assert.Equal(68, proc.Link1.UsedLoads);    // 4 module outputs + 64 DMX channels
        }
    }
}
