using System.Collections.Generic;
using System.Linq;
using TurboSuite.Shared.Constants;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.OneLine;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ControlRenderDataFactory (Section 2, #8 data-flow) — PanelResult + BrandConfig → the planner's
    // name→ControlPanelRenderData lookup. Pure mapping; shades need no entry (rendered off their unit).
    public class ControlRenderDataFactoryTests
    {
        private static ModuleResult Mod(string part) => new ModuleResult { PartNumber = part, DimmingType = "ELV" };

        [Fact]
        public void MapsAPd8ProcessorPanelToTilesLvSlotAndRole()
        {
            var panel = new PanelResult
            {
                PanelName = "1-A",
                SelectedPanelSize = 8,
                SpecialCompartmentPanelSizes = new HashSet<int> { 8 },
                SelectedSpecialDevice = "Processor",
                SpecialDevicePartNumbers = new Dictionary<string, string> { ["Processor"] = "HQP7-2" },
                Modules = new List<ModuleResult> { Mod("LQSE-4A-D"), Mod("LQSE-4A-D"), Mod("LQSE-4S10-SW") },
                IsProcessor = true,
            };

            var rd = ControlRenderDataFactory.BuildPanels(new[] { panel }, brand: null)["1-A"];

            Assert.Equal("PD8", rd.PartNumber);          // brand null → fallback
            Assert.Equal("3/8", rd.FillText);
            Assert.Equal(8, rd.ModuleTiles.Count);       // 5 empty + 3 filled
            // Bottom-up fill: empties at the top, then modules reversed (module 1 lands at the bottom).
            Assert.All(rd.ModuleTiles.Take(5), t => Assert.Null(t));
            Assert.Equal(new[] { "LQSE-4S10-SW", "LQSE-4A-D", "LQSE-4A-D" }, rd.ModuleTiles.Skip(5));
            Assert.Equal(new[] { "HQP7-2" }, rd.LvSlots); // one LV slot, the processor
            Assert.True(rd.HostsProcessor);
            Assert.Equal(Roles.ControlPanelDetail, rd.EnclosureRole);
        }

        [Fact]
        public void MapsAnLv21DualCompartmentPanelToTwoLvSlotsAndTheLv21Role()
        {
            var panel = new PanelResult
            {
                PanelName = "1-B",
                SelectedPanelSize = 21,
                SpecialCompartmentPanelSizes = new HashSet<int> { 21 },
                DualCompartmentPanelSizes = new HashSet<int> { 21 },
                SelectedSpecialDevice = "Processor",
                SelectedSpecialDevice2 = "Empty",
                SpecialDevicePartNumbers = new Dictionary<string, string> { ["Processor"] = "HQP7-2" },
                Modules = new List<ModuleResult>(),
                IsProcessor = true,
            };

            var rd = ControlRenderDataFactory.BuildPanels(new[] { panel }, brand: null)["1-B"];

            Assert.Equal(new[] { "HQP7-2", "EMPTY" }, rd.LvSlots);   // dual LV compartment; second is empty
            Assert.Equal(Roles.ControlLv21Detail, rd.EnclosureRole);
        }
    }
}
