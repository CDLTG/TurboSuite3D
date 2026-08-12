using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Phase 3d — DALI DinSlot injection into PanelAllocationService.
    //
    //  Part 1 (CHARACTERIZATION): pins the circuit-path math DALI perturbs — the panel-count
    //  recommendation (ceil(zoneTotalModules / defaultSize)) and the override add-a-panel loop — BEFORE
    //  the change, so a DALI regression to the lighting path can't slip through green. These call the
    //  existing signature only; they must pass on today's code.
    //
    //  Part 2 (DALI): the additive injection — a `zone → placed DALI loops` map lands display-only,
    //  slot-occupying, bus-labeled modules that are EXCLUDED from the BOM roll-up and the link budget
    //  (ordered/linked via the job-wide DaliSolver demand instead). Added after the implementation.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class PanelAllocationDaliCharacterizationTests
    {
        private static ZonesCircuitData C(string number, string panel, string type = "ELV")
            => new ZonesCircuitData { CircuitNumber = number, PanelName = panel, DimmingType = type };

        /// <summary>Lutron: ELV cap 4, default panel size 8. 34 ELV circuits → 9 modules
        /// (ceil(34·1.05/4)), which is more than one size-8 panel holds → 2 recommended panels 1-A/1-B.
        /// Pins the panel-count recommendation that DALI's modules feed into.</summary>
        [Fact]
        public void MultiPanelRecommendation_FromCircuitModules()
        {
            var circuits = Enumerable.Range(1, 34).Select(i => C(i.ToString(), "ZONE 1")).ToList();

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(circuits, BrandConfig.Lutron);

            Assert.Empty(unassigned);
            var loc = Assert.Single(result.Locations);
            Assert.Equal(9, loc.TotalModules);
            Assert.Equal(new[] { "1-A", "1-B" }, loc.Panels.Select(p => p.PanelName));
        }

        /// <summary>18 ELV circuits → 5 modules, one size-8 panel by default. Overriding 1-A down to a
        /// size-4 panel (capacity 4 &lt; 5) forces the allocator to add 1-B. Pins the override
        /// add-a-panel loop DALI's extra modules also trigger.</summary>
        [Fact]
        public void OverrideShrinkingAPanel_AddsAnother()
        {
            var circuits = Enumerable.Range(1, 18).Select(i => C(i.ToString(), "ZONE 1")).ToList();
            var overrides = new Dictionary<string, int> { ["1-A"] = 4 };

            var (result, _) = PanelAllocationService.BuildPanelBreakdown(circuits, BrandConfig.Lutron, overrides);

            var loc = Assert.Single(result.Locations);
            Assert.Equal(5, loc.TotalModules);
            Assert.Equal(new[] { "1-A", "1-B" }, loc.Panels.Select(p => p.PanelName));
            Assert.Equal(4, loc.Panels[0].SelectedPanelSize);
        }
    }

    public class PanelAllocationDaliInjectionTests
    {
        private static ZonesCircuitData C(string number, string panel, string type = "ELV")
            => new ZonesCircuitData { CircuitNumber = number, PanelName = panel, DimmingType = type };

        private static IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> Map(
            params (int Zone, DaliPanelModule[] Modules)[] entries)
            => entries.ToDictionary(e => e.Zone, e => (IReadOnlyList<DaliPanelModule>)e.Modules);

        private static DaliPanelModule Loop(string name, int loads = 12) => new DaliPanelModule(name, loads);

        /// <summary>A zone with no dimming circuits still gets a panel when the designer assigned a DALI
        /// loop to it — the union of circuit-zones and DALI-zones is what makes a DALI-only zone appear.
        /// The module occupies a slot, is labeled by its loop, and is tagged OrderedBySubsystem.</summary>
        [Fact]
        public void DaliOnlyZone_GetsAPanelWithABusLabeledModule()
        {
            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData>(), BrandConfig.Lutron,
                daliModulesByZone: Map((3, new[] { Loop("North Bus") })));

            Assert.Empty(unassigned);
            var loc = Assert.Single(result.Locations);
            Assert.Equal(3, loc.LocationNumber);
            Assert.Equal(1, loc.TotalModules);
            var module = Assert.Single(Assert.Single(loc.Panels).Modules);
            Assert.Equal("DALI", module.DimmingType);
            Assert.True(module.OrderedBySubsystem);
            Assert.Equal("North Bus", module.CircuitNumbersDisplay);   // labeled by loop, not circuits
        }

        /// <summary>In a mixed zone the DALI module lands AFTER the dimming modules and both count toward
        /// the slot total, but the DALI module is excluded from the link budget (DeviceCount/LoadCount)
        /// and the BOM roll-up — it is ordered by the job-wide demand, not here.</summary>
        [Fact]
        public void MixedZone_DaliCountsForSlotsButNotForBomOrLink()
        {
            // 4 ELV circuits → 1 Lutron module (cap 4); + 1 DALI loop.
            var circuits = Enumerable.Range(1, 4).Select(i => C(i.ToString(), "ZONE 1")).ToList();

            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Lutron,
                daliModulesByZone: Map((1, new[] { Loop("Kitchen") })));

            var panel = Assert.Single(Assert.Single(result.Locations).Panels);
            Assert.Equal(2, panel.TotalModuleCount);            // slot occupancy includes DALI
            Assert.Equal("DALI", panel.Modules.Last().DimmingType);  // appended last
            Assert.Equal(1, panel.DeviceCount);                 // link: ELV only, DALI excluded
            Assert.Equal(4, panel.LoadCount);                   // link: ELV module's 4 slots, DALI excluded

            // BOM roll-up: only the ELV part (Lutron's), never the DALI module.
            var parts = PanelAllocationService.GroupModulesByPartNumber(panel.Modules).ToList();
            Assert.Equal(new[] { "LQSE-4A5-120-D" }, parts.Select(p => p.PartNumber));
        }

        /// <summary>DALI modules bump the panel-count recommendation exactly like dimming modules: a zone
        /// whose dimming fills one panel gets a second panel when a DALI module is added.</summary>
        [Fact]
        public void DaliModule_BumpsThePanelCount_WhenTheFirstPanelIsFull()
        {
            // 30 ELV circuits → 8 Lutron modules = a full size-8 panel (1 panel on its own).
            var circuits = Enumerable.Range(1, 30).Select(i => C(i.ToString(), "ZONE 1")).ToList();

            var (withoutDali, _) = PanelAllocationService.BuildPanelBreakdown(circuits, BrandConfig.Lutron);
            Assert.Single(withoutDali.Locations[0].Panels);     // 8 modules → exactly one size-8 panel

            var (withDali, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Lutron, daliModulesByZone: Map((1, new[] { Loop("Hall") })));

            var loc = Assert.Single(withDali.Locations);
            Assert.Equal(9, loc.TotalModules);                  // 8 dimming + 1 DALI
            Assert.Equal(new[] { "1-A", "1-B" }, loc.Panels.Select(p => p.PanelName));
        }

        /// <summary>The additive guarantee: a null or empty DALI map leaves the circuit path byte-identical
        /// to the pre-3d signature — same panels, same module counts.</summary>
        [Fact]
        public void NullOrEmptyMap_IsIdenticalToTheCircuitOnlyPath()
        {
            var circuits = Enumerable.Range(1, 34).Select(i => C(i.ToString(), "ZONE 1")).ToList();

            var (baseline, _) = PanelAllocationService.BuildPanelBreakdown(circuits, BrandConfig.Lutron);
            var (empty, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Lutron,
                daliModulesByZone: new Dictionary<int, IReadOnlyList<DaliPanelModule>>());

            Assert.Equal(baseline.AllPanels.Select(p => p.PanelName), empty.AllPanels.Select(p => p.PanelName));
            Assert.Equal(baseline.AllPanels.Sum(p => p.Modules.Count), empty.AllPanels.Sum(p => p.Modules.Count));
        }

        /// <summary>A map entry with an empty loop list conjures no panel — a zone appears only when it has
        /// real dimming circuits or real DALI modules.</summary>
        [Fact]
        public void EmptyLoopList_DoesNotConjureAPanel()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData>(), BrandConfig.Lutron,
                daliModulesByZone: Map((5, Array.Empty<DaliPanelModule>())));

            Assert.Empty(result.Locations);
        }

        /// <summary>Multiple DALI loops in one zone each take a slot, all labeled and all excluded from the
        /// link budget.</summary>
        [Fact]
        public void MultipleDaliLoopsInAZone_EachTakeASlot()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData>(), BrandConfig.Lutron,
                daliModulesByZone: Map((2, new[] { Loop("A"), Loop("B"), Loop("C") })));

            var loc = Assert.Single(result.Locations);
            Assert.Equal(3, loc.TotalModules);
            Assert.Equal(0, loc.Panels.Sum(p => p.DeviceCount));   // all three excluded from link
            Assert.All(loc.Panels.SelectMany(p => p.Modules), m => Assert.True(m.OrderedBySubsystem));
        }
    }
}
