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
                new BomExtras { KeypadTallies = Tally.Of(("HQRD-W3BD", 3)) });

            Assert.Equal(
                new[] { "Processors", "Panels", "Modules", "Accessories", "Keypads" },
                Headers(bom));
        }

        /// <summary>Shades order into their own section, apart from Accessories: the QSPS-10PNL recommended
        /// by the shade subsystem lands under a "Shades" header and never in Accessories.</summary>
        [Fact]
        public void ShadesGetTheirOwnSection()
        {
            var shades = ShadeSolver.Solve(new List<ShadeLocationTally>
            {
                new ShadeLocationTally("SHADE 1", 33),
                new ShadeLocationTally("SHADE 2", 4)
            });
            var bom = Build(TwoFullPanels(), Lutron,
                new BomExtras { SubsystemDemands = new[] { shades } });

            Assert.Contains("Shades", Headers(bom));

            var line = Assert.Single(Section(bom, "Shades"));
            Assert.Equal("QSPS-10PNL", line.PartNumber);
            Assert.Equal(5, line.Quantity);   // ceil(33/10) + ceil(4/10)

            Assert.DoesNotContain(Section(bom, "Accessories"), l => l.PartNumber == "QSPS-10PNL");
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

        /// <summary>
        /// Keypads are one line per catalog number, and <b>nothing else</b> splits them. This section
        /// used to print the literal words "Keypad" and "Two-Gang Keypad" against blank part numbers —
        /// a quantity on a purchasing document with nothing to order against.
        ///
        /// Note the gang counts are set here and do NOT appear: a two-gang keypad is a different model
        /// with its own catalog number, so the lines separate on their own, and "Two Gang" is left
        /// doing the one job it is actually for — doubling a device on the link math.
        /// </summary>
        [Fact]
        public void KeypadLinesAreOnePerCatalogNumber()
        {
            var bom = Build(TwoFullPanels(), Lutron, new BomExtras
            {
                KeypadCount = 5,
                TwoGangKeypadCount = 2,
                KeypadTallies = Tally.Of(("HQRD-W3BD", 5), ("HQRD-W6BRL", 2))
            });

            var keypads = Section(bom, "Keypads");
            Assert.Equal(2, keypads.Count);
            Assert.Equal(("HQRD-W3BD", 5), (keypads[0].PartNumber, keypads[0].Quantity));
            Assert.Equal(("HQRD-W6BRL", 2), (keypads[1].PartNumber, keypads[1].Quantity));
            Assert.All(keypads, k => Assert.False(k.IsWarning));
        }

        /// <summary>Descriptions ride through from the family type, so a keypad line reads as a part
        /// rather than a bare code. Blank where the family had no field left to describe the slot.</summary>
        [Fact]
        public void KeypadLinesCarryTheFamilyDescription()
        {
            var keypads = Section(Build(TwoFullPanels(), Lutron, new BomExtras
            {
                KeypadTallies = Tally.Described(
                    ("HQRD-W3BD", "3-Button Keypad with Raise/Lower", 5),
                    ("HQRD-BTN-KIT", "Button Kit", 10),
                    ("HQRD-2G-FACE", "", 5))
            }), "Keypads");

            Assert.Equal("3-Button Keypad with Raise/Lower", keypads[0].Description);
            Assert.Equal("Button Kit", keypads[1].Description);
            Assert.Equal("", keypads[2].Description);
        }

        /// <summary>The type's own words beat the generic per-category text, being the more specific of
        /// the two. A repeater whose family says nothing still gets the product description.</summary>
        [Fact]
        public void FamilyDescriptionWinsOverTheCategoryDefault()
        {
            var accessories = Section(Build(TwoFullPanels(), Lutron, new BomExtras
            {
                HybridRepeaters = new ControlDeviceGroup
                {
                    DeviceCount = 3,
                    Tallies = Tally.Described(("HQK-REP", "868 MHz Hybrid Repeater", 1), ("HQR-REP-120", "", 2))
                }
            }), "Accessories");

            Assert.Equal("868 MHz Hybrid Repeater",
                accessories.Single(a => a.PartNumber == "HQK-REP").Description);
            Assert.Equal("HWQS Hybrid Wired/Wireless RF System Repeater",
                accessories.Single(a => a.PartNumber == "HQR-REP-120").Description);
        }

        /// <summary>Gang counts alone produce no keypad section — the order comes from catalog numbers,
        /// not from the link-math counts, so a job whose keypads are all uncollected prints nothing
        /// rather than an unorderable placeholder.</summary>
        [Fact]
        public void GangCountsAloneDoNotMakeKeypadLines()
        {
            var bom = Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadCount = 5, TwoGangKeypadCount = 2 });

            Assert.DoesNotContain("Keypads", Headers(bom));
        }

        /// <summary>
        /// A keypad type with no catalog number is still ordered — the keypads are placed and the
        /// quantity is real. The part column falls back to the generic word, so the row reads
        /// <c>4 · Keypad</c> on both surfaces rather than as a quantity of nothing.
        ///
        /// The only difference is the flag: the design surface marks the row so the missing number
        /// gets filled in, the issued document just prints it.
        /// </summary>
        [Fact]
        public void KeypadWithNoCatalogNumberFallsBackToTheGenericPartName()
        {
            var tallies = Tally.Named((null, "Seetouch 5-Button", 4));

            var design = Section(Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadTallies = tallies, Audience = BomAudience.DesignSurface }), "Keypads");
            var issued = Section(Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadTallies = tallies, Audience = BomAudience.IssuedDocument }), "Keypads");

            Assert.Equal(("Keypad", 4), (Assert.Single(design).PartNumber, design[0].Quantity));
            Assert.True(design[0].IsWarning);
            Assert.Equal("", design[0].Description);

            // Quantity and part number never depend on audience — only the flag does.
            Assert.Equal(("Keypad", 4), (Assert.Single(issued).PartNumber, issued[0].Quantity));
            Assert.False(issued[0].IsWarning);
            Assert.Equal("", issued[0].Description);
        }

        /// <summary>Repeaters get their own generic fallback rather than the keypad one, and keep their
        /// product description — that text is what the part IS, not commentary about the design.</summary>
        [Fact]
        public void RepeaterWithNoCatalogNumberFallsBackToItsOwnGenericName()
        {
            var line = Section(Build(TwoFullPanels(), Lutron, new BomExtras
            {
                HybridRepeaters = new ControlDeviceGroup
                {
                    DeviceCount = 2,
                    Tallies = Tally.Named((null, "Hybrid Repeater Type A", 2))
                }
            }), "Accessories").Single(a => a.PartNumber == "Hybrid Repeater");

            Assert.Equal(2, line.Quantity);
            Assert.Equal("HWQS Hybrid Wired/Wireless RF System Repeater", line.Description);
        }

        /// <summary>An unparseable quantity rule still explains itself on the design surface — unlike a
        /// missing catalog number, the number on the line is a fallback rather than the authored
        /// intent, so the row says which slot to look at.</summary>
        [Fact]
        public void BadQuantityRuleExplainsItselfOnTheDesignSurface()
        {
            var tallies = new List<ControlDeviceTally>
            {
                new ControlDeviceTally
                {
                    CatalogNumber = "HQRD-BTN-KIT",
                    TypeName = "Seetouch 5-Button",
                    Quantity = 4,
                    Diagnostic = "Seetouch 5-Button — Catalog Qty2 \"banana\": Unrecognized format"
                }
            };

            var design = Assert.Single(Section(Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadTallies = tallies, Audience = BomAudience.DesignSurface }), "Keypads"));
            var issued = Assert.Single(Section(Build(TwoFullPanels(), Lutron,
                new BomExtras { KeypadTallies = tallies, Audience = BomAudience.IssuedDocument }), "Keypads"));

            Assert.True(design.IsWarning);
            Assert.Contains("Catalog Qty2", design.Description);

            Assert.False(issued.IsWarning);
            Assert.Equal("", issued.Description);
            Assert.Equal(design.Quantity, issued.Quantity);
        }

        /// <summary>Repeaters group by catalog number too. The part number used to be read off the
        /// FIRST instance only, so a two-model job ordered them all as whichever model happened to be
        /// collected first.</summary>
        [Fact]
        public void RepeatersGroupByCatalogNumberRatherThanTakingTheFirst()
        {
            var accessories = Section(Build(TwoFullPanels(), Lutron, new BomExtras
            {
                HybridRepeaters = Tally.RepeaterGroup(5, ("HQR-REP-120", 3), ("HQK-REP", 2))
            }), "Accessories");

            Assert.Equal(3, accessories.Single(a => a.PartNumber == "HQR-REP-120").Quantity);
            Assert.Equal(2, accessories.Single(a => a.PartNumber == "HQK-REP").Quantity);
        }

        /// <summary>
        /// Devices on a link and parts on an order are different numbers, and the link math must take
        /// the former. A repeater type declaring a mounting bracket in a second slot orders two parts
        /// per device — summing the order rows would size Clear Connect for hardware that is not there.
        ///
        /// Four devices is one CC-A link (the cap is four). Eight <i>parts</i> would be two, and would
        /// wrongly recommend a second processor.
        /// </summary>
        [Fact]
        public void LinkMathCountsDevicesNotOrderedParts()
        {
            var panels = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };
            var extras = new BomExtras
            {
                HybridRepeaters = Tally.RepeaterGroup(4, ("HQR-REP-120", 4), ("HQR-BRACKET", 4))
            };

            Assert.Equal(4, extras.HybridRepeaterCount);
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(panels, extras));
        }

        /// <summary>Crestron declares no power supply, no harnesses and gets no repeater line, so the
        /// Accessories section collapses away rather than printing an empty header.</summary>
        [Fact]
        public void AccessoriesSectionOmittedWhenBrandContributesNone()
        {
            var panel = new PanelResult { PanelName = "1-A", SelectedPanelSize = 7 };
            panel.Modules.Add(new ModuleResult { DimmingType = "ELV", PartNumber = "CLX-2DIMU8", ModuleCapacity = 8 });

            var bom = Build(new List<PanelResult> { panel }, Crestron,
                new BomExtras { HybridRepeaters = Tally.Repeaters(4) });

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
            var extras = new BomExtras { HybridRepeaters = Tally.Repeaters(2, "HQR-W") };

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

        /// <summary>Loads cap a link at 512. Fourteen full 9-module panels are 504 loads — one link;
        /// the fifteenth crosses into a second, while devices (135) are still well inside the 99×2
        /// they now occupy. Two links still fit one HQP7-2.</summary>
        [Fact]
        public void LoadCapDrivesLinkCount()
        {
            var panels = Enumerable.Range(1, 15)
                .Select(i => LutronPanel($"P{i:00}", 9, Enumerable.Repeat(("ELV", 4), 9).ToArray()))
                .ToList();

            Assert.Equal(540, panels.Sum(p => p.LoadCount));
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(panels, new BomExtras()));
        }

        /// <summary>Hybrid repeaters ride a separate Clear Connect link, so they ADD a link rather than
        /// consuming QS capacity: 1 QS link + 1 CCA link = 2 ⇒ still one processor, but a third link
        /// tips it. Four repeaters fit one CC-A link (Lutron 369-351b); the fifth needs a second, and
        /// that is enough to force a second processor on its own.</summary>
        [Fact]
        public void HybridRepeatersAddAClearConnectLink()
        {
            var panels = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };

            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(panels, new BomExtras()));
            Assert.Equal(1, ControlBomBuilder.CalculateRecommendedProcessors(
                panels, new BomExtras { HybridRepeaters = Tally.Repeaters(4) }));
            Assert.Equal(2, ControlBomBuilder.CalculateRecommendedProcessors(
                panels, new BomExtras { HybridRepeaters = Tally.Repeaters(5) }));
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
        /// placed-below-recommended case below. 1 QS link + 2 Clear Connect links (5 repeaters at 4
        /// per link) = 3 links ⇒ 2 processors.</summary>
        private static List<PanelResult> UnderPlacedJob()
        {
            var panel = LutronPanel("1-A", 8, ("ELV", 4));
            panel.SelectedSpecialDevice = "Processor";
            return new List<PanelResult> { panel };
        }

        private static BomExtras UnderPlacedExtras(BomAudience audience) => new BomExtras
        {
            HybridRepeaters = Tally.Repeaters(5, "HQR-W"),
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

        /// <summary>A brand with no compartments at all (Crestron declares no special devices) must not
        /// have a Lutron interface fall through onto its BOM. "No compartment defined for this brand"
        /// is not the same as "this part has no compartment anywhere".</summary>
        [Fact]
        public void LutronInterfaceDoesNotLandOnACrestronBom()
        {
            var panels = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };

            var bom = Build(panels, Crestron, With(Dmx(4, 100), BomAudience.IssuedDocument));

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
        /// devices, channels are switch legs. Enough channels alone must move the processor count.
        ///
        /// Sized in whole interfaces at Lutron's 32 channels each, because an interface is what packs:
        /// a link holds 16 of them (16 × 32 = 512 legs), two links fill a processor, so 32 interfaces
        /// fit one and 33 need a second. Stating it as "one interface with 1000 channels" would be an
        /// object no solver can emit and no link can hold.</summary>
        [Fact]
        public void ChannelsAloneCanForceAnotherProcessor()
        {
            var panels = OnePanel();

            int WithInterfaces(int interfaces) => ControlBomBuilder.CalculateRecommendedProcessors(
                panels,
                new BomExtras
                {
                    SubsystemDemands = new List<ControlSubsystemDemand> { Dmx(interfaces, interfaces * 32) }
                });

            Assert.Equal(1, WithInterfaces(31));
            Assert.Equal(2, WithInterfaces(33));
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

    /// <summary>
    /// QS-link power-supply sizing (Phase 2b). Supplies are no longer one-per-processor: each QS link
    /// nets its device PDU against a supply's +75, the processor's −8 lands on its first QS link, and
    /// the count is ceil(|net|/75) summed over QS links plus one per all-wireless processor.
    /// Feasibility is a global slot check. The shipped under-order these pin is the 67/68 boundary.
    /// </summary>
    public class ControlBomPowerSupplyTests : ControlBomTestBase
    {
        private const string Qsps = "QSPS-DH-1-75-H";

        /// <summary>A processor panel of the given size with no modules — a clean stage for the keypad
        /// pour, whose PDU is what the boundary tests turn on.</summary>
        private static List<PanelResult> Processor(int size)
        {
            var panel = LutronPanel("1-A", size);
            panel.SelectedSpecialDevice = "Processor";
            return new List<PanelResult> { panel };
        }

        private static BomLineItem Supply(List<PanelResult> panels, BomExtras extras)
            => Section(Build(panels, Lutron, extras), "Accessories").Single(a => a.PartNumber == Qsps);

        /// <summary>The exact shipped boundary. One processor (−8) plus 67 keypads (−67) is exactly −75
        /// on the link → one supply; the 68th keypad crosses 75 into a second. Both keypads counts land
        /// on the one QS link (well inside its 99 devices), so the whole draw nets on a single supply.</summary>
        [Theory]
        [InlineData(67, 1)]
        [InlineData(68, 2)]
        public void KeypadPduSizesSuppliesAtTheSeventyFiveBoundary(int keypads, int expected)
        {
            // PD8 alone is one slot; the 68-keypad case needs two, so keep the sizing test off the
            // feasibility path with an issued document (no warning) and assert the quantity only.
            var supply = Supply(Processor(8),
                new BomExtras { KeypadCount = keypads, Audience = BomAudience.IssuedDocument });

            Assert.Equal(expected, supply.Quantity);
        }

        /// <summary>The −8 alone orders a supply: a processor with no keypads and no interfaces still
        /// draws 8 PDU on its first QS link, ceil(8/75) = 1. This is the one-per-processor floor the old
        /// code hard-coded, now falling out of the arithmetic.</summary>
        [Fact]
        public void LonelyProcessorStillOrdersOneSupply()
            => Assert.Equal(1, Supply(Processor(8), new BomExtras()).Quantity);

        /// <summary>No processor sited → zero supplies, and the line is dropped rather than shown as a
        /// bare "0". Design surface, where zero lines normally survive: the processor line's "0 of N
        /// placed" already says to site one, and the supply — derived, not placed — has nothing of its
        /// own to annotate.</summary>
        [Fact]
        public void ZeroSupplyLineIsSuppressedOnDesignSurface()
        {
            var noProcessor = new List<PanelResult> { LutronPanel("1-A", 8, ("ELV", 4)) };
            var accessories = Section(
                Build(noProcessor, Lutron, new BomExtras { Audience = BomAudience.DesignSurface }),
                "Accessories");

            Assert.DoesNotContain(accessories, a => a.PartNumber == Qsps);
        }

        /// <summary>All-wireless safeguard: five repeaters take both of the processor's links to Clear
        /// Connect, leaving no QS link to carry the −8 — but the box still needs power, so the processor
        /// contributes one supply directly.</summary>
        [Fact]
        public void AllWirelessProcessorOrdersOneSupply()
        {
            var supply = Supply(Processor(8), new BomExtras { HybridRepeaters = Tally.Repeaters(5) });
            Assert.Equal(1, supply.Quantity);
        }

        /// <summary>Clear Connect carries no PDU budget, so the wireless keypads riding it draw nothing —
        /// a processor with a QS link and a pile of wireless devices still orders just the one supply its
        /// −8 forces.</summary>
        [Fact]
        public void WirelessDevicesDrawNoPdu()
        {
            var supply = Supply(Processor(8),
                new BomExtras { HybridRepeaters = Tally.Repeaters(1), WirelessDeviceCount = 40 });
            Assert.Equal(1, supply.Quantity);
        }

        /// <summary>Global feasibility: a PD4 holds one supply slot, so a job needing two (68 keypads
        /// past the boundary) is flagged on the design surface — a new warning shape naming the
        /// shortfall, because the fix is a bigger panel, not another placement.</summary>
        [Fact]
        public void ShortfallAgainstPanelSlotsWarnsOnDesignSurface()
        {
            var supply = Supply(Processor(4),
                new BomExtras { KeypadCount = 68, Audience = BomAudience.DesignSurface });

            Assert.Equal(2, supply.Quantity);
            Assert.True(supply.IsWarning);
            Assert.Contains("(2 needed, panels hold 1)", supply.Description);
        }

        /// <summary>The LV21 holds two supply slots where a DIN panel holds one, so the same two-supply
        /// demand that overflows a PD4 fits an LV21 cleanly — no warning, description untouched.</summary>
        [Fact]
        public void Lv21TwoSlotsAbsorbWhatAPd4Cannot()
        {
            var supply = Supply(Processor(0),
                new BomExtras { KeypadCount = 68, Audience = BomAudience.DesignSurface });

            Assert.Equal(2, supply.Quantity);
            Assert.False(supply.IsWarning);
            Assert.Equal("DIN Rail Power Supply with Wire Harnesses", supply.Description);
        }

        /// <summary>Two processors in one LV21's two compartments are two HQP7-2s — two power groups
        /// that cannot share a supply — so the job orders two, one per processor's −8, and the LV21's
        /// two slots hold them (feasible). Counting per panel would order both processors but one
        /// supply. Matches the two-separate-panels case exactly.</summary>
        [Fact]
        public void TwoProcessorsInOneLv21OrderTwoSupplies()
        {
            var lv21 = LutronPanel("1-A", 0);
            lv21.SelectedSpecialDevice = "Processor";
            lv21.SelectedSpecialDevice2 = "Processor";

            var supply = Supply(new List<PanelResult> { lv21 },
                new BomExtras { Audience = BomAudience.DesignSurface });

            Assert.Equal(2, supply.Quantity);
            Assert.False(supply.IsWarning);   // LV21 holds two supplies
        }

        /// <summary>The shortfall is a presentation concern: the issued document orders the same two
        /// supplies with no commentary, exactly as audience is required to behave everywhere else.</summary>
        [Fact]
        public void IssuedDocumentOrdersTheShortfallWithoutCommentary()
        {
            var supply = Supply(Processor(4),
                new BomExtras { KeypadCount = 68, Audience = BomAudience.IssuedDocument });

            Assert.Equal(2, supply.Quantity);
            Assert.False(supply.IsWarning);
            Assert.Equal("DIN Rail Power Supply with Wire Harnesses", supply.Description);
        }
    }
}
