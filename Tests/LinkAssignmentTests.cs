using System.Collections.Generic;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for LinkAssignmentService (Core/Zones/Services/LinkAssignmentService.cs).
    //  Assigns panels to processor links (2 per processor: QS by default) and tallies devices/loads,
    //  reserving trailing links as "Clear Connect Type A" when wireless (hybrid repeater) devices
    //  exist. Mutates PanelResult.Link1/Link2 in place — assert on those after the call.
    //
    //  For me (Claude): ProcessorLink.MaxDevices=99, MaxLoads=512. A PanelResult's DeviceCount =
    //  Modules.Count and LoadCount = Σ ModuleCapacity, so I shape those by adding ModuleResults.
    //  IsProcessor is what makes a panel spawn links (the real ViewModel sets it); a panel can be a
    //  processor without a special compartment. Derivations inline.
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

        [Fact]
        public void NoProcessorPanels_NoLinksCreated()
        {
            var plain = new PanelResult { PanelName = "1-A" }; // IsProcessor false
            LinkAssignmentService.AssignAndAggregate(new List<PanelResult> { plain }, keypadCount: 0);
            Assert.Null(plain.Link1);
            Assert.Null(plain.Link2);
        }

        [Fact]
        public void EmptyPanelList_NoThrow()
            => LinkAssignmentService.AssignAndAggregate(new List<PanelResult>(), keypadCount: 0);

        [Fact]
        public void SingleProcessor_GetsTwoQsLinks_AndAggregatesToFirst()
        {
            var proc = Proc("1-A", modules: 3, cap: 4); // DeviceCount=3, LoadCount=12
            LinkAssignmentService.AssignAndAggregate(new List<PanelResult> { proc }, keypadCount: 0);

            Assert.Equal("QS", proc.Link1.LinkType);
            Assert.Equal("QS", proc.Link2.LinkType);
            // The lone panel fits on Link 1: 3 devices, 12 loads. Link 2 stays empty.
            Assert.Equal(3, proc.Link1.UsedDevices);
            Assert.Equal(12, proc.Link1.UsedLoads);
            Assert.Equal(0, proc.Link2.UsedDevices);
        }

        [Fact]
        public void Keypads_GroupOnLinkWithMostDeviceHeadroom()
        {
            var proc = Proc("1-A", modules: 3, cap: 4);
            LinkAssignmentService.AssignAndAggregate(new List<PanelResult> { proc }, keypadCount: 10);

            // Link 1 carries the panel (3 devices, room 96); Link 2 is empty (room 99) → keypads land there.
            Assert.Equal(3, proc.Link1.UsedDevices);
            Assert.Equal(10, proc.Link2.UsedDevices);
        }

        [Fact]
        public void WirelessDevices_ReserveLastLinkAsClearConnect()
        {
            var proc = Proc("1-A", modules: 2, cap: 4); // DeviceCount=2, LoadCount=8
            LinkAssignmentService.AssignAndAggregate(
                new List<PanelResult> { proc }, keypadCount: 0, hybridRepeaterCount: 3);

            // ceil(3/99)=1 CC-A link, taken from the last link backward → Link 2 becomes CC-A with the
            // 3 repeaters; the panel falls to the remaining QS link (Link 1).
            Assert.Equal("Clear Connect Type A", proc.Link2.LinkType);
            Assert.Equal(3, proc.Link2.UsedDevices);
            Assert.Equal(0, proc.Link2.UsedLoads);
            Assert.Equal("QS", proc.Link1.LinkType);
            Assert.Equal(2, proc.Link1.UsedDevices);
            Assert.Equal(8, proc.Link1.UsedLoads);
        }

        [Fact]
        public void SpecialCompartmentDigitalIO_CountsAsOneExtraDevice()
        {
            var proc = Proc("1-A", modules: 1, cap: 4); // DeviceCount=1
            proc.SpecialCompartmentPanelSizes = new HashSet<int> { 4 };
            proc.SelectedPanelSize = 4;                 // → HasSpecialCompartment true
            proc.SelectedSpecialDevice = "Digital I/O"; // QSE-IO counts as 1 device on its link

            LinkAssignmentService.AssignAndAggregate(new List<PanelResult> { proc }, keypadCount: 0);

            // 1 panel device + 1 special-compartment device = 2 on Link 1.
            Assert.Equal(2, proc.Link1.UsedDevices);
            Assert.Equal(4, proc.Link1.UsedLoads);
        }

        [Fact]
        public void NonSpecialDeviceSelection_NoExtraDevice()
        {
            var proc = Proc("1-A", modules: 1, cap: 4);
            proc.SpecialCompartmentPanelSizes = new HashSet<int> { 4 };
            proc.SelectedPanelSize = 4;
            proc.SelectedSpecialDevice = "Empty"; // neither Digital I/O nor DMX → no increment

            LinkAssignmentService.AssignAndAggregate(new List<PanelResult> { proc }, keypadCount: 0);

            Assert.Equal(1, proc.Link1.UsedDevices);
        }
    }
}
