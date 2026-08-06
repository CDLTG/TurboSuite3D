using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the control BOM builder (Core/Zones/Services/ControlBomBuilder.cs) — the
    //  parts list derived from a panel allocation. This is a purchasing document: a wrong quantity
    //  here is a wrong order.
    //
    //  These tests exist because the BOM used to be built TWICE — once in PanelBreakdownTabViewModel
    //  for the TurboZones window, once in BomCollectorService for the TurboDocs PDF — and the two
    //  copies had already drifted (the window annotated a processor shortfall, the PDF silently
    //  rounded the quantity up). They were merged into one builder; this suite is what keeps them
    //  merged. The single surviving per-consumer difference is BomExtras.Audience, which governs
    //  PRESENTATION only — if a quantity ever depends on it, that is the bug this suite exists for.
    //
    //  For me (Claude): panels are built directly rather than through PanelAllocationService, so a
    //  failure here is a BOM bug and never an allocator bug. Section ORDER is part of the contract —
    //  the list is render-ready and neither consumer re-groups it.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fixture helpers: real BrandConfigs, hand-built panels.</summary>
    public abstract class ControlBomTestBase
    {
        protected static readonly BrandConfig Lutron = BrandConfig.CreateLutron(useDedicatedRelayModule: false);
        protected static readonly BrandConfig Crestron = BrandConfig.Crestron;

        /// <summary>A Lutron panel of the given module capacity, carrying <paramref name="moduleSpecs"/>
        /// as (dimmingType, moduleCapacity) pairs. Compartment sizes match the real Lutron config
        /// (special on 0/4/8, dual only on the LV21).</summary>
        protected static PanelResult LutronPanel(string name, int size,
            params (string Type, int Cap)[] moduleSpecs)
        {
            var panel = new PanelResult
            {
                PanelName = name,
                SelectedPanelSize = size,
                SpecialCompartmentPanelSizes = new HashSet<int> { 0, 4, 8 },
                DualCompartmentPanelSizes = new HashSet<int> { 0 }
            };
            foreach (var (type, cap) in moduleSpecs)
            {
                panel.Modules.Add(new ModuleResult
                {
                    DimmingType = type,
                    PartNumber = Lutron.GetModulePartNumber(type),
                    ModuleCapacity = cap
                });
            }
            return panel;
        }

        protected static List<BomLineItem> Build(List<PanelResult> panels, BrandConfig brand,
            BomExtras? extras = null)
            => ControlBomBuilder.Build(panels, brand, extras ?? new BomExtras());

        /// <summary>The non-header lines under a given section header, in order.</summary>
        protected static List<BomLineItem> Section(List<BomLineItem> bom, string category)
            => bom.Where(i => !i.IsHeader && i.Category == category).ToList();

        protected static List<string> Headers(List<BomLineItem> bom)
            => bom.Where(i => i.IsHeader).Select(i => i.Description).ToList();
    }

    /// <summary>Document shape: which sections appear, in what order, and when they are omitted.</summary>
    public class ControlBomStructureTests : ControlBomTestBase
    {
        /// <summary>Two panels with a processor sited on the first — the ordinary designed job.</summary>
        private static List<PanelResult> TwoFullPanels()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 8, ("ELV", 4), ("ELV", 4), ("0-10V", 4), ("Relay", 4)),
                LutronPanel("2-A", 8, ("ELV", 4), ("0-10V", 4))
            };
            panels[0].SelectedSpecialDevice = "Processor";
            return panels;
        }

        [Fact]
        public void SectionsAppearInDocumentOrder()
        {
            var bom = Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadCount = 3 });

            Assert.Equal(
                new[] { "Processors", "Panels", "Modules", "Accessories", "Keypads" },
                Headers(bom));
        }

        /// <summary>An issued document lists only what is actually being ordered, so a job with no
        /// processor sited drops the section entirely rather than printing a blank-quantity row.</summary>
        [Fact]
        public void IssuedDocumentOmitsUnplacedProcessorSection()
        {
            var bom = Build(new List<PanelResult>(), Lutron);

            Assert.DoesNotContain("Processors", Headers(bom));
            Assert.Empty(Section(bom, "Processors"));
        }

        /// <summary>The design surface keeps the line at zero — "0 placed" is precisely the thing the
        /// user needs to see, and the window is where they act on it.</summary>
        [Fact]
        public void DesignSurfaceKeepsUnplacedProcessorLine()
        {
            var bom = Build(new List<PanelResult>(), Lutron,
                new BomExtras { Audience = BomAudience.DesignSurface });

            var line = Assert.Single(Section(bom, "Processors"));
            Assert.Equal(0, line.Quantity);
            Assert.Equal("HQP7-2", line.PartNumber);
        }

        /// <summary>Keypads are omitted entirely at zero rather than printing a 0-quantity line.</summary>
        [Fact]
        public void KeypadSectionOmittedWhenNoKeypads()
        {
            var bom = Build(TwoFullPanels(), Lutron);
            Assert.DoesNotContain("Keypads", Headers(bom));
        }

        [Fact]
        public void KeypadLinesSplitSingleAndTwoGang()
        {
            var bom = Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadCount = 5, TwoGangKeypadCount = 2 });

            var keypads = Section(bom, "Keypads");
            Assert.Equal(2, keypads.Count);
            Assert.Equal(5, keypads[0].Quantity);
            Assert.Equal("Keypad", keypads[0].Description);
            Assert.Equal(2, keypads[1].Quantity);
            Assert.Equal("Two-Gang Keypad", keypads[1].Description);
        }

        /// <summary>Crestron declares no power supply, no harnesses and gets no repeater line, so the
        /// Accessories section collapses away rather than printing an empty header.</summary>
        [Fact]
        public void AccessoriesSectionOmittedWhenBrandContributesNone()
        {
            var panel = new PanelResult { PanelName = "1-A", SelectedPanelSize = 7 };
            panel.Modules.Add(new ModuleResult { DimmingType = "ELV", PartNumber = "CLX-2DIMU8", ModuleCapacity = 8 });

            var bom = Build(new List<PanelResult> { panel }, Crestron,
                new BomExtras { HybridRepeaterCount = 4 });

            Assert.DoesNotContain("Accessories", Headers(bom));
        }
    }

    /// <summary>Quantities — the part of the BOM that costs money when it is wrong.</summary>
    public class ControlBomQuantityTests : ControlBomTestBase
    {
        [Fact]
        public void PanelsGroupByCapacityLargestFirst()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 4),
                LutronPanel("2-A", 8),
                LutronPanel("3-A", 8)
            };

            var lines = Section(Build(panels, Lutron), "Panels");

            Assert.Equal(2, lines.Count);
            Assert.Equal("PD8-59F-120", lines[0].PartNumber);   // 8 before 4 — descending capacity
            Assert.Equal(2, lines[0].Quantity);
            Assert.Equal("PD4-36F-120", lines[1].PartNumber);
            Assert.Equal(1, lines[1].Quantity);
        }

        /// <summary>0-10V and Relay both resolve to LQSE-4T5 in the default Lutron config, so they must
        /// collapse to ONE order line — two lines for the same part number is a double-order.</summary>
        [Fact]
        public void ModulesSharingAPartNumberCollapseToOneLine()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 8, ("0-10V", 4), ("Relay", 4), ("Relay", 4), ("ELV", 4))
            };

            var lines = Section(Build(panels, Lutron), "Modules");

            Assert.Equal(2, lines.Count);
            var t5 = Assert.Single(lines, l => l.PartNumber == "LQSE-4T5-120-D");
            Assert.Equal(3, t5.Quantity);                       // 1 × 0-10V + 2 × Relay
            var a5 = Assert.Single(lines, l => l.PartNumber == "LQSE-4A5-120-D");
            Assert.Equal(1, a5.Quantity);
        }

        /// <summary>With a dedicated relay module configured, Relay splits back onto its own part.</summary>
        [Fact]
        public void DedicatedRelayModuleSplitsTheCollapsedLine()
        {
            var brand = BrandConfig.CreateLutron(useDedicatedRelayModule: true);
            var panel = new PanelResult { PanelName = "1-A", SelectedPanelSize = 8 };
            foreach (var type in new[] { "0-10V", "Relay" })
            {
                panel.Modules.Add(new ModuleResult
                {
                    DimmingType = type,
                    PartNumber = brand.GetModulePartNumber(type),
                    ModuleCapacity = 4
                });
            }

            var lines = Section(Build(new List<PanelResult> { panel }, brand), "Modules");

            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, l => l.PartNumber == "LQSE-4T5-120-D");
            Assert.Contains(lines, l => l.PartNumber == "LQSE-4S8-120-D");
        }

        /// <summary>One wire harness per panel, keyed to that panel's size.</summary>
        [Fact]
        public void WireHarnessesFollowPanelSizes()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 8),
                LutronPanel("2-A", 8),
                LutronPanel("3-A", 5)
            };

            var accessories = Section(Build(panels, Lutron), "Accessories");

            Assert.Equal(2, accessories.Single(a => a.PartNumber == "PDW-QS-8").Quantity);
            Assert.Equal(1, accessories.Single(a => a.PartNumber == "PDW-QS-5").Quantity);
        }

        /// <summary>Power supply quantity tracks the BOM processor count, not the placed count — the
        /// order has to include a supply for a processor the user has not sited yet.</summary>
        [Fact]
        public void PowerSupplyQuantityMatchesProcessorQuantity()
        {
            var panels = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };
            panels[0].SelectedSpecialDevice = "Processor";

            var bom = Build(panels, Lutron);

            int processorQty = Section(bom, "Processors").Single().Quantity;
            var supply = Section(bom, "Accessories").Single(a => a.PartNumber == "QSPS-DH-1-75-H");
            Assert.Equal(processorQty, supply.Quantity);
        }

        /// <summary>Compartment devices are ordered, but Processor and Empty are not — the processor has
        /// its own section, and Empty is not a part.</summary>
        [Fact]
        public void SpecialDevicesExcludeProcessorAndEmpty()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 8, ("ELV", 4)),
                LutronPanel("2-A", 8, ("ELV", 4)),
                LutronPanel("3-A", 8, ("ELV", 4))
            };
            panels[0].SelectedSpecialDevice = "Processor";
            panels[1].SelectedSpecialDevice = "Digital I/O";
            panels[2].SelectedSpecialDevice = "Empty";

            var accessories = Section(Build(panels, Lutron), "Accessories");

            Assert.Single(accessories, a => a.PartNumber == "QSE-IO");
            Assert.DoesNotContain(accessories, a => a.PartNumber == "HQP7-2");
        }

        /// <summary>The LV21 carries two compartment slots; both are counted.</summary>
        [Fact]
        public void DualCompartmentPanelCountsBothSlots()
        {
            var panel = LutronPanel("1-A", 0);
            panel.SelectedSpecialDevice = "Digital I/O";
            panel.SelectedSpecialDevice2 = "DMX";
            Assert.True(panel.HasDualSpecialCompartment);

            var accessories = Section(Build(new List<PanelResult> { panel }, Lutron), "Accessories");

            Assert.Equal(1, accessories.Single(a => a.PartNumber == "QSE-IO").Quantity);
            Assert.Equal(1, accessories.Single(a => a.PartNumber == "QSE-CI-DMX").Quantity);
        }

        /// <summary>Hybrid repeaters are a Lutron-only line.</summary>
        [Fact]
        public void HybridRepeatersAreLutronOnly()
        {
            var panels = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };
            var extras = new BomExtras { HybridRepeaterCount = 2, HybridRepeaterPartNumber = "HQR-W" };

            var lutron = Section(Build(panels, Lutron, extras), "Accessories");
            Assert.Equal(2, lutron.Single(a => a.PartNumber == "HQR-W").Quantity);

            var crestronPanel = new PanelResult { PanelName = "1-A", SelectedPanelSize = 7 };
            var crestron = Section(Build(new List<PanelResult> { crestronPanel }, Crestron, extras), "Accessories");
            Assert.DoesNotContain(crestron, a => a.PartNumber == "HQR-W");
        }
    }

    /// <summary>Processor count: the QS-link roll-up, and the placed-vs-required reconciliation that
    /// was the one behavioural difference between the two old builders.</summary>
    public class ControlBomProcessorTests : ControlBomTestBase
    {
        private static List<PanelResult> SmallJob()
            => new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4), ("0-10V", 4)) };

        /// <summary>A small job needs 1 link, so 1 processor: ceil(1 link / 2 links per processor).</summary>
        [Fact]
        public void SmallJobNeedsOneProcessor()
            => Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(SmallJob(), new BomExtras()));

        /// <summary>Loads cap a link at 512. 129 modules × 4 slots = 516 loads ⇒ 2 QS links ⇒ still
        /// 1 processor (2 links fit one HQP7-2).</summary>
        [Fact]
        public void LoadCapDrivesLinkCount()
        {
            var panel = LutronPanel("1-A", 8,
                Enumerable.Repeat(("ELV", 4), 129).ToArray());

            Assert.Equal(516, panel.LoadCount);
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(
                new List<PanelResult> { panel }, new BomExtras()));
        }

        /// <summary>Hybrid repeaters ride a separate Clear Connect link, so they ADD a link rather than
        /// consuming QS capacity: 2 QS links + 1 CCA link = 3 ⇒ 2 processors.</summary>
        [Fact]
        public void HybridRepeatersAddAClearConnectLink()
        {
            var panel = LutronPanel("1-A", 8, Enumerable.Repeat(("ELV", 4), 129).ToArray());
            var panels = new List<PanelResult> { panel };

            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(panels, new BomExtras()));
            Assert.Equal(2, ControlBomBuilder.CalculateRecommendedProcessors(
                panels, new BomExtras { HybridRepeaterCount = 1 }));
        }

        /// <summary>Keypads count as QS devices — two-gang counts as two.</summary>
        [Fact]
        public void TwoGangKeypadsCountAsTwoDevices()
        {
            var panels = SmallJob();
            // 2 modules + 98 keypad devices = 100 > 99 ⇒ 2 links ⇒ still 1 processor.
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(
                panels, new BomExtras { TwoGangKeypadCount = 49 }));
        }

        /// <summary>A job needing 2 processors but with only 1 sited: the fixture for every
        /// placed-below-recommended case below. 129 modules ⇒ 2 QS links, + 1 CCA link ⇒ 2 processors.</summary>
        private static List<PanelResult> UnderPlacedJob()
        {
            var panel = LutronPanel("1-A", 8, Enumerable.Repeat(("ELV", 4), 129).ToArray());
            panel.SelectedSpecialDevice = "Processor";
            return new List<PanelResult> { panel };
        }

        private static BomExtras UnderPlacedExtras(BomAudience audience) => new BomExtras
        {
            HybridRepeaterCount = 1,
            HybridRepeaterPartNumber = "HQR-W",
            Audience = audience
        };

        /// <summary>Placing MORE processors than required is honored — the designer sited them.</summary>
        [Fact]
        public void PlacedProcessorsAboveRecommendationSetTheQuantity()
        {
            var panels = new List<PanelResult>
            {
                LutronPanel("1-A", 8, ("ELV", 4)),
                LutronPanel("2-A", 8, ("ELV", 4))
            };
            panels[0].SelectedSpecialDevice = "Processor";
            panels[1].SelectedSpecialDevice = "Processor";

            var line = Section(Build(panels, Lutron), "Processors").Single();
            Assert.Equal(2, line.Quantity);
            Assert.False(line.IsWarning);
        }

        /// <summary>And placing FEWER is honored too. A processor's location cannot be derived — it is
        /// an assignment the designer makes — so the Panel Breakdown is the source of truth in both
        /// directions, and the recommendation never silently inflates an order.</summary>
        [Fact]
        public void PlacedProcessorsBelowRecommendationSetTheQuantity()
        {
            var panels = UnderPlacedJob();
            Assert.Equal(2, ControlBomBuilder.CalculateRecommendedProcessors(
                panels, UnderPlacedExtras(BomAudience.IssuedDocument)));

            var line = Section(Build(panels, Lutron, UnderPlacedExtras(BomAudience.IssuedDocument)),
                "Processors").Single();

            Assert.Equal(1, line.Quantity);   // what was designed, not the 2 recommended
        }

        /// <summary>The power supply rides the same rule — one per PLACED processor, so an under-placed
        /// job does not quietly order supplies for processors nobody sited.</summary>
        [Fact]
        public void PowerSupplyFollowsPlacedNotRecommended()
        {
            var accessories = Section(
                Build(UnderPlacedJob(), Lutron, UnderPlacedExtras(BomAudience.IssuedDocument)),
                "Accessories");

            Assert.Equal(1, accessories.Single(a => a.PartNumber == "QSPS-DH-1-75-H").Quantity);
        }

        /// <summary>Design surface: the shortfall is spelled out and flagged so the window can style it.
        /// This is the safety net that replaces inflating the quantity.</summary>
        [Fact]
        public void DesignSurfaceMarksAndExplainsAShortfall()
        {
            var line = Section(
                Build(UnderPlacedJob(), Lutron, UnderPlacedExtras(BomAudience.DesignSurface)),
                "Processors").Single();

            Assert.True(line.IsWarning);
            Assert.Contains("(1 of 2 placed)", line.Description);
        }

        /// <summary>Issued document: same quantity, no commentary. BomPdfService renders Description
        /// verbatim into a purchasing document and has no warning styling to pair it with.</summary>
        [Fact]
        public void IssuedDocumentLeavesTheDescriptionClean()
        {
            var line = Section(
                Build(UnderPlacedJob(), Lutron, UnderPlacedExtras(BomAudience.IssuedDocument)),
                "Processors").Single();

            Assert.False(line.IsWarning);
            Assert.Equal("HomeWorks QSX 2-Link Processor", line.Description);
        }

        /// <summary>Audience changes presentation only — never a quantity.</summary>
        [Fact]
        public void AudienceDoesNotChangeQuantities()
        {
            var design = Build(UnderPlacedJob(), Lutron, UnderPlacedExtras(BomAudience.DesignSurface));
            var issued = Build(UnderPlacedJob(), Lutron, UnderPlacedExtras(BomAudience.IssuedDocument));

            var designQty = design.Where(i => !i.IsHeader && i.Quantity > 0)
                .Select(i => (i.PartNumber, i.Quantity)).OrderBy(x => x.PartNumber).ToList();
            var issuedQty = issued.Where(i => !i.IsHeader)
                .Select(i => (i.PartNumber, i.Quantity)).OrderBy(x => x.PartNumber).ToList();

            Assert.Equal(designQty, issuedQty);
        }

        /// <summary>No shortfall ⇒ no annotation even on the design surface.</summary>
        [Fact]
        public void DesignSurfaceIsSilentWhenFullyPlaced()
        {
            var panels = SmallJob();
            panels[0].SelectedSpecialDevice = "Processor";

            var line = Section(
                Build(panels, Lutron, new BomExtras { Audience = BomAudience.DesignSurface }),
                "Processors").Single();

            Assert.False(line.IsWarning);
            Assert.Equal("HomeWorks QSX 2-Link Processor", line.Description);
        }
    }

    /// <summary>
    /// Control-subsystem demand — what a subsystem solves for itself (DMX today, DALI later).
    ///
    /// For me (Claude): the rule is PLACEMENT WINS WHEREVER PLACEMENT IS POSSIBLE, identical to the
    /// processor rule two classes up. A QSE-CI-DMX is a compartment device, so the solve is a
    /// REQUIREMENT that annotates the line ("1 of 4 placed"), never an order that overrides it. That
    /// annotation is the signal telling the designer to go find somewhere to put the other three —
    /// which is a decision no solver can make, and which they can always act on, since overriding a
    /// panel to LV21 frees two compartments and the allocator re-homes the displaced modules.
    ///
    /// A part with NO compartment (the DALI DIN module, when it lands) has no placement to defer to
    /// and is emitted at its solved quantity. That is the same rule, not an exception to it.
    /// </summary>
    public class ControlBomSubsystemTests : ControlBomTestBase
    {
        private static ControlSubsystemDemand Dmx(int interfaces, int channels) =>
            new ControlSubsystemDemand("DMX",
                parts: new List<DemandPart>
                {
                    new DemandPart("QSE-CI-DMX", interfaces, DemandMount.LvCompartment)
                },
                linkDevices: interfaces, linkLoads: channels);

        private static BomExtras With(ControlSubsystemDemand demand, BomAudience audience) =>
            new BomExtras
            {
                Audience = audience,
                SubsystemDemands = new List<ControlSubsystemDemand> { demand }
            };

        private static List<PanelResult> OnePanel(string? placedDevice = null)
        {
            var panel = LutronPanel("1-A", 8, ("ELV", 4));
            if (placedDevice != null) panel.SelectedSpecialDevice = placedDevice;
            return new List<PanelResult> { panel };
        }

        /// <summary>The order follows the designer. TurboDMX solving four interfaces does not put four
        /// on the purchase order — it puts a requirement next to what was placed.</summary>
        [Fact]
        public void OrderedQuantityFollowsPlacementNotTheSolve()
        {
            var bom = Build(OnePanel("DMX"), Lutron, With(Dmx(4, 100), BomAudience.IssuedDocument));

            var line = Assert.Single(Section(bom, "Accessories"), i => i.PartNumber == "QSE-CI-DMX");
            Assert.Equal(1, line.Quantity);
        }

        /// <summary>Every placed compartment counts, and the solve never caps them — placing more than
        /// solved orders more, the same way an over-placed processor does.</summary>
        [Fact]
        public void PlacingMoreThanSolvedOrdersMore()
        {
            var panels = OnePanel("DMX");
            panels.Add(LutronPanel("2-A", 8, ("ELV", 4)));
            panels[1].SelectedSpecialDevice = "DMX";

            var bom = Build(panels, Lutron, With(Dmx(1, 20), BomAudience.IssuedDocument));

            Assert.Equal(2, Assert.Single(Section(bom, "Accessories"),
                i => i.PartNumber == "QSE-CI-DMX").Quantity);
        }

        /// <summary>A compartment device with no subsystem behind it behaves identically — placement
        /// has always driven these lines, and the subsystem changes nothing about that.</summary>
        [Fact]
        public void UnclaimedSpecialDevicesStillCountFromPlacement()
        {
            var panels = OnePanel("Digital I/O");

            var bom = Build(panels, Lutron, With(Dmx(2, 40), BomAudience.IssuedDocument));

            Assert.Equal(1, Assert.Single(Section(bom, "Accessories"), i => i.PartNumber == "QSE-IO").Quantity);
        }

        /// <summary>Placing fewer than the solve calls for is flagged where it can be fixed, with the
        /// same "(N of M placed)" shape a processor shortfall uses. This annotation is the whole
        /// mechanism: it is how a designer learns to go free up a compartment.</summary>
        [Fact]
        public void ShortfallIsAnnotatedOnTheDesignSurface()
        {
            var bom = Build(OnePanel("DMX"), Lutron, With(Dmx(3, 90), BomAudience.DesignSurface));

            var line = Assert.Single(Section(bom, "Accessories"), i => i.PartNumber == "QSE-CI-DMX");
            Assert.True(line.IsWarning);
            Assert.Equal(1, line.Quantity);
            Assert.Contains("(1 of 3 placed)", line.Description);
        }

        /// <summary>Nothing placed still shows the requirement on the design surface — a zero line is
        /// exactly how an unplaced processor surfaces, and it is the only way the designer finds out
        /// the job needs interfaces at all.</summary>
        [Fact]
        public void NothingPlacedStillShowsTheRequirement()
        {
            var bom = Build(OnePanel(), Lutron, With(Dmx(4, 120), BomAudience.DesignSurface));

            var line = Assert.Single(Section(bom, "Accessories"), i => i.PartNumber == "QSE-CI-DMX");
            Assert.Equal(0, line.Quantity);
            Assert.True(line.IsWarning);
            Assert.Contains("(0 of 4 placed)", line.Description);
        }

        /// <summary>...and orders nothing on the issued document, where a zero-quantity line is
        /// stripped. A job that never sited an interface buys none — the Phase 0 rule, unchanged.</summary>
        [Fact]
        public void NothingPlacedOrdersNothing()
        {
            var bom = Build(OnePanel(), Lutron, With(Dmx(4, 120), BomAudience.IssuedDocument));

            Assert.DoesNotContain(bom, i => i.PartNumber == "QSE-CI-DMX");
        }

        /// <summary>A subsystem part with no compartment to sit in has no placement to defer to, so it
        /// is emitted at its solved quantity. Forward cover for the DALI DIN module.</summary>
        [Fact]
        public void PartsWithNoCompartmentFollowTheSolve()
        {
            var demand = new ControlSubsystemDemand("DALI",
                parts: new List<DemandPart>
                {
                    new DemandPart("LQSE-4DALI", 3, DemandMount.DinSlot, "DALI Module")
                });

            var bom = Build(OnePanel(), Lutron, With(demand, BomAudience.IssuedDocument));

            Assert.Equal(3, Assert.Single(Section(bom, "Accessories"),
                i => i.PartNumber == "LQSE-4DALI").Quantity);
        }

        /// <summary>The issued PDF carries no design-state commentary — the audience rule from Phase 0
        /// applies to subsystem lines too.</summary>
        [Fact]
        public void ShortfallIsNotAnnotatedOnTheIssuedDocument()
        {
            var line = Assert.Single(
                Section(Build(OnePanel("DMX"), Lutron, With(Dmx(3, 90), BomAudience.IssuedDocument)),
                        "Accessories"),
                i => i.PartNumber == "QSE-CI-DMX");

            Assert.False(line.IsWarning);
            Assert.DoesNotContain("placed", line.Description);
        }

        /// <summary>The audience invariant, restated for subsystem lines: presentation may differ,
        /// quantities may not.</summary>
        [Fact]
        public void AudienceDoesNotChangeSubsystemQuantities()
        {
            int Qty(BomAudience a) => Section(
                Build(OnePanel("DMX"), Lutron, With(Dmx(3, 90), a)), "Accessories")
                .Single(i => i.PartNumber == "QSE-CI-DMX").Quantity;

            Assert.Equal(Qty(BomAudience.IssuedDocument), Qty(BomAudience.DesignSurface));
        }

        /// <summary>An unsolvable subsystem produces a warning line on the design surface — there is
        /// real hardware that will not make the order, and the reason is the subsystem's own.</summary>
        [Fact]
        public void UnsolvableSubsystemWarnsOnTheDesignSurface()
        {
            var demand = ControlSubsystemDemand.Unsolvable("DMX", "no decoder type is selected");

            var bom = Build(OnePanel(), Lutron, With(demand, BomAudience.DesignSurface));

            var warning = Assert.Single(Section(bom, "Accessories"), i => i.IsWarning);
            Assert.Contains("no decoder type is selected", warning.Description);
        }

        /// <summary>...but never on the issued document, where it is neither actionable nor orderable.
        /// The BOM still builds — that is the whole requirement.</summary>
        [Fact]
        public void UnsolvableSubsystemIsSilentOnTheIssuedDocument()
        {
            var demand = ControlSubsystemDemand.Unsolvable("DMX", "no decoder type is selected");

            var bom = Build(OnePanel(), Lutron, With(demand, BomAudience.IssuedDocument));

            Assert.NotEmpty(bom);
            Assert.DoesNotContain(bom, i => i.IsWarning);
            Assert.DoesNotContain(bom, i => i.Description != null && i.Description.Contains("decoder"));
        }

        /// <summary>A demand can carry parts AND a caveat — a DMX solve over partially zoned tape is
        /// complete for what it saw and still under-counts. Both must survive: the order needs the
        /// parts, the designer needs the caveat.</summary>
        [Fact]
        public void PartsAndACaveatBothSurviveOnTheDesignSurface()
        {
            var demand = new ControlSubsystemDemand("DMX",
                parts: new List<DemandPart>
                {
                    new DemandPart("QSE-CI-DMX", 2, DemandMount.LvCompartment)
                },
                linkDevices: 2, linkLoads: 60,
                diagnostic: "3 DMX fixtures are not in any Control Zone");

            var bom = Build(OnePanel("DMX"), Lutron, With(demand, BomAudience.DesignSurface));

            Assert.Equal(1, Assert.Single(Section(bom, "Accessories"),
                i => i.PartNumber == "QSE-CI-DMX").Quantity);   // placed, per the rule
            Assert.Contains(Section(bom, "Accessories"),
                i => i.IsWarning && i.Description.Contains("not in any Control Zone"));
        }

        /// <summary>Demand pressures the link budgets, and the two are independent: interfaces are QS
        /// devices, channels are switch legs. Enough channels alone must move the processor count.</summary>
        [Fact]
        public void ChannelsAloneCanForceAnotherProcessor()
        {
            var panels = OnePanel();

            int WithChannels(int channels) => ControlBomBuilder.CalculateRecommendedProcessors(
                panels, new BomExtras { SubsystemDemands = new List<ControlSubsystemDemand> { Dmx(1, channels) } });

            // One link carries 512 loads; two links fill one processor, so crossing 1024 needs a second.
            Assert.Equal(1, WithChannels(1000));
            Assert.Equal(2, WithChannels(1100));
        }

        /// <summary>No demand at all leaves every quantity exactly where Phase 0 left it — the seam is
        /// inert on the jobs that have no subsystem hardware, which is most of them.</summary>
        [Fact]
        public void NoDemandIsByteIdenticalToNoSeam()
        {
            var extras = new BomExtras { KeypadCount = 12, Audience = BomAudience.DesignSurface };
            var withNull = Build(OnePanel("Processor"), Lutron, extras);

            extras.SubsystemDemands = new List<ControlSubsystemDemand>
            {
                ControlSubsystemDemand.None("DMX")
            };
            var withEmpty = Build(OnePanel("Processor"), Lutron, extras);

            Assert.Equal(withNull.Select(i => (i.Category, i.PartNumber, i.Quantity, i.Description)),
                         withEmpty.Select(i => (i.Category, i.PartNumber, i.Quantity, i.Description)));
        }
    }

    /// <summary>Degenerate inputs — a BOM must not throw at the boundaries the callers can hit.</summary>
    public class ControlBomEdgeCaseTests : ControlBomTestBase
    {
        [Fact]
        public void NullPanelsYieldEmptyBom()
            => Assert.Empty(ControlBomBuilder.Build(null, Lutron, new BomExtras()));

        [Fact]
        public void NullBrandYieldsEmptyBom()
            => Assert.Empty(ControlBomBuilder.Build(new List<PanelResult>(), null, new BomExtras()));

        /// <summary>Null extras behaves as all-zero with the default (issued-document) audience, not as
        /// a crash — the PDF path builds extras from collector results that can legitimately be absent.
        /// With nothing placed and no keypads, only the panel and module lines survive.</summary>
        [Fact]
        public void NullExtrasTreatedAsEmpty()
        {
            var bom = ControlBomBuilder.Build(new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) },
                Lutron, null);

            Assert.Equal(new[] { "Panels", "Modules", "Accessories" }, Headers(bom));
            Assert.Empty(Section(bom, "Processors"));
            Assert.Equal(1, Section(bom, "Modules").Single().Quantity);
            // Accessories survives on the wire harness alone — the power supply is 0 and stripped.
            Assert.Equal("PDW-QS-8", Section(bom, "Accessories").Single().PartNumber);
        }
    }
}
