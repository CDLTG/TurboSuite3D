using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Shared.Constants;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.OneLine;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  ControlOneLinePlanner (Section 2, #4) — the pure horizontal-layout planner. These build a
    //  LinkPackResult directly (isolating the planner from the packer) + render-data dicts, and assert
    //  the shape: the processor-hosting panel is the head, downstream nodes follow rule-#5 order, keypads
    //  are a tail stub only where they landed, Clear Connect draws a wireless stub, and a shared HOME
    //  NETWORK node feeds CAT6 to each head.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class ControlOneLinePlannerTests
    {
        private static PackedLinkUnit U(string name, LinkCategory cat, int devices, int loads = 0)
            => new PackedLinkUnit(name, cat, devices, loads);

        private static PackedLink Qs(params PackedLinkUnit[] units)
            => new PackedLink(ProcessorLink.QsLinkType, units.Sum(u => u.Devices), units.Sum(u => u.Loads),
                units.Select(u => u.Name).ToArray(), units: units);

        private static PackedLink EmptyQs() => new PackedLink(ProcessorLink.QsLinkType, 0, 0, Array.Empty<string>());

        // A QS link carrying located keypads: the "Keypads" synthetic unit (what sets HasKeypads) plus the
        // per-keypad records the located list draws — the shape ControlLinkPacker.ToQsLink produces.
        private static PackedLink QsKeypads(KeypadRecord[] recs, params PackedLinkUnit[] units)
        {
            var all = units.Append(new PackedLinkUnit("Keypads", LinkCategory.Keypads, recs.Sum(r => r.Devices), 0)).ToArray();
            return new PackedLink(ProcessorLink.QsLinkType, all.Sum(u => u.Devices), all.Sum(u => u.Loads),
                all.Select(u => u.Name).ToArray(), units: all, keypadRecords: recs);
        }

        private static PackedLink Cca(int devices = 5)
            => new PackedLink(ProcessorLink.ClearConnectLinkType, devices, 0, Array.Empty<string>());

        private static ProcessorGroup Group(PackedLink l1, PackedLink l2, int location = 1)
            => new ProcessorGroup(location, l1, l2);

        private static ProcessorGroup GroupNamed(string procPanel, PackedLink l1, PackedLink l2, int location = 1)
            => new ProcessorGroup(location, l1, l2, procPanel);

        // An LV21 hosts processors but carries NO modules — it is no unit on any link, so only its NAME identifies it.
        private static ControlPanelRenderData Lv21(string name)
            => new ControlPanelRenderData(name, "HQ-LV21-120", "0/0", System.Array.Empty<string?>(),
                new[] { "HQP7-2", "HQP7-2" }, hostsProcessor: true, Roles.ControlLv21Detail);

        private static LinkPackResult Pack(params ProcessorGroup[] groups)
        {
            var flat = groups.SelectMany(g => new[] { g.Link1, g.Link2 }).ToList();
            return new LinkPackResult(flat, groups.Length, 0, groups);
        }

        private static IReadOnlyList<string?> Tiles(int n, string part = "LQSE-4A-D")
            => Enumerable.Repeat<string?>(part, n).ToList();

        private static ControlPanelRenderData Proc(string name, int modules = 8)
            => new ControlPanelRenderData(name, "PD8-59F-120", $"{modules}/8", Tiles(modules),
                new[] { "HQP7-2" }, hostsProcessor: true, Roles.ControlPanelDetail);

        private static ControlPanelRenderData Dimmer(string name, int modules = 6)
            => new ControlPanelRenderData(name, "PD9-59F-120", $"{modules}/9", Tiles(modules),
                new[] { "EMPTY" }, hostsProcessor: false, Roles.ControlPanelDetail);

        // A shade unit carries its motor count as Loads (ShadeSolver: devices = fill+1, loads = fill).
        private static PackedLinkUnit ShadeUnit(string name, int motors)
            => new PackedLinkUnit(name, LinkCategory.Shades, motors + 1, motors);

        // A shade unit carrying per-motor records (Phase E) — the shape ShadeSolver now produces.
        private static PackedLinkUnit ShadeUnitWithMotors(string name, params string[] circuits)
            => new PackedLinkUnit(name, LinkCategory.Shades, circuits.Length + 1, circuits.Length,
                circuits.Select(c => new ShadeMotorRecord(c, $"motor {c}")).ToList());

        private static ControlOneLineDrawing BuildOne(LinkPackResult pack,
            IReadOnlyDictionary<string, ControlPanelRenderData> panels)
            => Assert.Single(ControlOneLinePlanner.Build(pack, panels, "TurboControl"));

        /// <summary>The processor-hosting panel is the head: drawn once, leftmost, and never repeated as a
        /// downstream node even though it rides one of the links as a Modules unit.</summary>
        [Fact]
        public void ProcessorPanelIsTheHeadDrawnOnceAtTheLeft()
        {
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8), U("1-B", LinkCategory.Modules, 6)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A"), ["1-B"] = Dimmer("1-B") };

            var page = BuildOne(pack, panels);

            var head = Assert.Single(page.Panels, p => p.Name == "1-A");
            Assert.True(head.HostsProcessor);
            Assert.Equal(ControlOneLineGeometry.Layout.ProcessorColumnX, head.Center.X, 3);
            // "1-A" appears exactly once (not re-drawn downstream), "1-B" downstream to its right.
            Assert.Single(page.Panels, p => p.Name == "1-A");
            var down = Assert.Single(page.Panels, p => p.Name == "1-B");
            Assert.True(down.Center.X > head.Center.X);
        }

        /// <summary>Rule #5 — a scrambled link draws Modules before Shades (natural-name within a category),
        /// with the head excluded and keypads as a trailing stub, never a node.</summary>
        [Fact]
        public void DownstreamNodesFollowRuleFiveOrder()
        {
            // Landing order deliberately scrambled: shade, dimmer, proc-head, keypads.
            var link = Qs(
                ShadeUnit("1-D", 10),
                U("1-B", LinkCategory.Modules, 6),
                U("1-A", LinkCategory.Modules, 8),      // the head (hosts processor)
                U("Keypads", LinkCategory.Keypads, 12));
            var pack = Pack(Group(link, EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData>
            { ["1-A"] = Proc("1-A"), ["1-B"] = Dimmer("1-B") };

            var page = BuildOne(pack, panels);

            var dimmer = Assert.Single(page.Panels, p => p.Name == "1-B");
            var shade = Assert.Single(page.Shades, s => s.Name == "1-D");
            Assert.True(dimmer.Center.X < shade.Center.X, "Modules must draw before Shades");
            // keypads are a stub, not a node
            Assert.Contains(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
            Assert.DoesNotContain(page.Panels, p => p.Name == "Keypads");
        }

        /// <summary>Phase D: a link whose keypads carry per-keypad records draws the located list — one
        /// note per keypad ("[Switch ID] · Room · Type") — instead of the "ALL KEYPADS" stub. The homerun
        /// wiring note stays, and keypads are still never drawn as a node.</summary>
        [Fact]
        public void LocatedKeypads_DrawPerKeypadRows_NotTheStub()
        {
            var recs = new[]
            {
                new KeypadRecord("K1", "ENTRY", "seeTouch", location: 1),
                new KeypadRecord("K2", "GYM", "seeTouch", location: 1),
            };
            var pack = Pack(Group(QsKeypads(recs, U("1-A", LinkCategory.Modules, 8)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("[K1] ENTRY"));
            Assert.Contains(page.Notes, n => n.Text.Contains("[K2] GYM"));
            Assert.DoesNotContain(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
            Assert.Contains(page.Notes, n => n.Text.Contains("MAX 10 PER HOMERUN"));   // homerun note stays
            Assert.DoesNotContain(page.Panels, p => p.Name == "Keypads");
        }

        /// <summary>Phase E: a shade unit carrying per-motor records draws the motor list — one note per
        /// motor ("&lt;circuit&gt; · &lt;load name&gt;") rising from the tap — instead of the "n MOTORS" stub.</summary>
        [Fact]
        public void ShadeMotors_DrawPerMotorRows_NotTheStub()
        {
            var link = Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnitWithMotors("2-D", "M01", "M02", "M03"));
            var pack = Pack(Group(link, EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("[M01] motor M01"));
            Assert.Contains(page.Notes, n => n.Text.Contains("[M03] motor M03"));
            Assert.DoesNotContain(page.Notes, n => n.Text.Contains("MOTORS"));   // count stub replaced
        }

        /// <summary>A shade unit with no per-motor records keeps the "n MOTORS" count stub (count-only path).</summary>
        [Fact]
        public void ShadeWithoutMotorRecords_KeepsTheCountStub()
        {
            var link = Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("2-D", 3));
            var pack = Pack(Group(link, EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("3 MOTORS"));
        }

        /// <summary>Location-less keypads (a unit but no records) keep the legacy stub — the fallback the
        /// plan specifies when a link's keypads have no mapped location.</summary>
        [Fact]
        public void LocationLessKeypads_KeepTheLegacyStub()
        {
            var link = Qs(U("1-A", LinkCategory.Modules, 8), U("Keypads", LinkCategory.Keypads, 5));
            var pack = Pack(Group(link, EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
        }

        /// <summary>The text-block case: Link 1 QS carries modules + shade + keypads; Link 2 is the wireless
        /// ("RF") link. The keypad stub lands on the QS link, the wireless stub on the RF leg. The RF leg is QS
        /// WIRE to a repeater (abstracted today), so it carries a QS marker — never a Clear Connect marker
        /// (there is no CC cable).</summary>
        [Fact]
        public void WirelessLinkDrawsWirelessStubAsQsWireNotClearConnect()
        {
            var pack = Pack(Group(
                Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10), U("Keypads", LinkCategory.Keypads, 10)),
                Cca()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("WIRELESS KEYPADS"));
            Assert.Contains(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
            Assert.DoesNotContain(page.Markers, m => m.Type == ControlWireType.ClearConnect);
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.QsControlLink);
            Assert.Contains(page.Shades, s => s.Name == "1-D");
        }

        /// <summary>A link with no keypad unit gets no keypad stub.</summary>
        [Fact]
        public void NoKeypadStubWhenLinkHasNoKeypads()
        {
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.DoesNotContain(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
        }

        /// <summary>Multi-processor: each bay's head sits at the left column on its own row, and each head draws
        /// its OWN Ethernet-to-Home-Network stub (a CAT6 marker + a labeled note) out its left edge — there is no
        /// shared switch node tying the heads together (per the Lutron reference sheet).</summary>
        [Fact]
        public void EachProcessorDrawsItsOwnEthernetStub()
        {
            var pack = Pack(
                Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs(), location: 1),
                Group(Qs(U("2-A", LinkCategory.Modules, 8)), EmptyQs(), location: 2));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A"), ["2-A"] = Proc("2-A") };

            var page = BuildOne(pack, panels);

            var heads = page.Panels.Where(p => p.HostsProcessor).ToList();
            Assert.Equal(2, heads.Count);
            Assert.All(heads, h => Assert.Equal(ControlOneLineGeometry.Layout.ProcessorColumnX, h.Center.X, 3));
            Assert.NotEqual(heads[0].Center.Y, heads[1].Center.Y);
            // One Ethernet stub per head: a CAT6 marker + a labeled note each.
            Assert.Equal(2, page.Markers.Count(m => m.Type == ControlWireType.Cat6));
            Assert.Equal(2, page.Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
        }

        /// <summary>An LV21's two processor compartments arrive as two same-named <see cref="ProcessorGroup"/>s.
        /// The planner MERGES them into ONE drawn head, found by name (the LV21 carries no modules, so it is no
        /// unit on any link and the old unit-scan missed it — drawing two fallback P# boxes). Its four links fan
        /// from the single head; the downstream dimmers still draw.</summary>
        [Fact]
        public void Lv21TwoCompartmentsDrawOneHeadNotTwoFallbackBoxes()
        {
            var pack = Pack(
                GroupNamed("1-A", Qs(U("1-B", LinkCategory.Modules, 6)), EmptyQs()),
                GroupNamed("1-A", Qs(U("1-C", LinkCategory.Modules, 6)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData>
            { ["1-A"] = Lv21("1-A"), ["1-B"] = Dimmer("1-B"), ["1-C"] = Dimmer("1-C") };

            var page = BuildOne(pack, panels);

            // Exactly ONE head, and it is the named LV21 — not two, and not a "P#" stub.
            var head = Assert.Single(page.Panels, p => p.HostsProcessor);
            Assert.Equal("1-A", head.Name);
            Assert.Equal(ControlOneLineGeometry.Layout.ProcessorColumnX, head.Center.X, 3);
            Assert.DoesNotContain(page.Panels, p => p.Name.StartsWith("P", StringComparison.Ordinal) && p.HostsProcessor && p.Name != "1-A");
            // Both compartments' dimmers draw downstream, to the head's right.
            Assert.Contains(page.Panels, p => p.Name == "1-B" && p.Center.X > head.Center.X);
            Assert.Contains(page.Panels, p => p.Name == "1-C" && p.Center.X > head.Center.X);
        }

        // A 4-link LV21 with three LIVE QS links (link1+link2 of compartment 1, link1 of compartment 2) = 3 rows.
        private static ProcessorGroup[] Lv21ThreeRows(string enc, string a, string b, string c)
            => new[]
            {
                GroupNamed(enc, Qs(U(a, LinkCategory.Modules, 6)), Qs(U(b, LinkCategory.Modules, 6))),
                GroupNamed(enc, Qs(U(c, LinkCategory.Modules, 6)), EmptyQs()),
            };

        /// <summary>Two 3-row LV21s (6 rows) don't co-fit on one 4-row page, and an enclosure is indivisible, so
        /// each lands on its own sheet. Rows re-base per page (both heads bottom-align to row 0 → identical Y),
        /// and HOME NETWORK is repeated on each sheet so no wire crosses the page boundary.</summary>
        [Fact]
        public void TwoFullEnclosuresThatDoNotCoFitPaginateOnePerSheet()
        {
            var groups = Lv21ThreeRows("1-A", "1-B", "1-C", "1-D")
                .Concat(Lv21ThreeRows("2-A", "2-B", "2-C", "2-D")).ToArray();
            var pack = Pack(groups);
            var panels = new Dictionary<string, ControlPanelRenderData>
            {
                ["1-A"] = Lv21("1-A"), ["1-B"] = Dimmer("1-B"), ["1-C"] = Dimmer("1-C"), ["1-D"] = Dimmer("1-D"),
                ["2-A"] = Lv21("2-A"), ["2-B"] = Dimmer("2-B"), ["2-C"] = Dimmer("2-C"), ["2-D"] = Dimmer("2-D"),
            };

            var pages = ControlOneLinePlanner.Build(pack, panels, "TurboControl");

            Assert.Equal(2, pages.Count);
            Assert.All(pages, p => Assert.Equal(2, p.PageCount));
            Assert.Equal(1, pages[0].PageIndex);
            Assert.Equal(2, pages[1].PageIndex);

            var head1 = Assert.Single(pages[0].Panels, p => p.HostsProcessor);
            var head2 = Assert.Single(pages[1].Panels, p => p.HostsProcessor);
            Assert.Equal("1-A", head1.Name);
            Assert.Equal("2-A", head2.Name);
            // Indivisible: each enclosure's own three dimmers live wholly on its sheet, none leak to the other.
            Assert.All(new[] { "1-B", "1-C", "1-D" }, n => Assert.Contains(pages[0].Panels, p => p.Name == n));
            Assert.All(new[] { "1-B", "1-C", "1-D" }, n => Assert.DoesNotContain(pages[1].Panels, p => p.Name == n));
            Assert.All(new[] { "2-B", "2-C", "2-D" }, n => Assert.Contains(pages[1].Panels, p => p.Name == n));
            // Per-page row re-basing: both heads sit at the same Y band (row 0), not 28' apart down a shared sheet.
            Assert.Equal(head1.Center.Y, head2.Center.Y, 3);
            // Each sheet's head draws its own Ethernet stub — self-contained, nothing crosses the page break.
            Assert.Single(pages[0].Notes, n => n.Text.Contains("ETHERNET LINK"));
            Assert.Single(pages[1].Notes, n => n.Text.Contains("ETHERNET LINK"));
        }

        /// <summary>Greedy in-order pack: three 2-row PD8s (6 rows) fill a sheet with the FIRST TWO (4 rows), then
        /// spill the third to a second sheet — proving enclosures share a page up to the 4-row cap before breaking.</summary>
        [Fact]
        public void EnclosuresShareASheetUntilTheFourRowCapThenSpill()
        {
            // Each PD8: link1 = head + a dimmer (live), link2 = a dimmer (live) ⇒ 2 live rows.
            ProcessorGroup Pd8(string h, string d1, string d2)
                => Group(Qs(U(h, LinkCategory.Modules, 8), U(d1, LinkCategory.Modules, 6)),
                         Qs(U(d2, LinkCategory.Modules, 6)));
            var pack = Pack(Pd8("A", "A2", "A3"), Pd8("B", "B2", "B3"), Pd8("C", "C2", "C3"));
            var panels = new Dictionary<string, ControlPanelRenderData>
            {
                ["A"] = Proc("A"), ["A2"] = Dimmer("A2"), ["A3"] = Dimmer("A3"),
                ["B"] = Proc("B"), ["B2"] = Dimmer("B2"), ["B3"] = Dimmer("B3"),
                ["C"] = Proc("C"), ["C2"] = Dimmer("C2"), ["C3"] = Dimmer("C3"),
            };

            var pages = ControlOneLinePlanner.Build(pack, panels, "TurboControl");

            Assert.Equal(2, pages.Count);
            // Page 1 packs TWO heads (A + B = 4 rows, the cap); page 2 holds the third (C).
            var page1Heads = pages[0].Panels.Where(p => p.HostsProcessor).Select(p => p.Name).OrderBy(n => n).ToList();
            var page2Heads = pages[1].Panels.Where(p => p.HostsProcessor).Select(p => p.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "A", "B" }, page1Heads);
            Assert.Equal(new[] { "C" }, page2Heads);
            // Ethernet stubs are per head: page 1 has two heads (two stubs), page 2 one.
            Assert.Equal(2, pages[0].Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
            Assert.Single(pages[1].Notes, n => n.Text.Contains("ETHERNET LINK"));
        }
    }
}
