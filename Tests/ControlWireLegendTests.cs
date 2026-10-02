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
    //  ControlWireLegend (Section 2, #5) — the per-job wire legend's roster + dense numbering + the
    //  layout planner. MARKER-TIED roster (decided 2026-10-01): QS + CAT6 always; Sivoia shade link only
    //  with shades; Clear Connect only with wireless; PanelControlLink + DaliLoop stay benched (never
    //  rostered). These pin the presence rules, the dense skip-absent numbering, and that the planner
    //  stamps the legend's numbers onto the drawn markers so legend ↔ markers stay 1:1.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class ControlWireLegendTests
    {
        private static PackedLinkUnit U(string name, LinkCategory cat, int devices, int loads = 0)
            => new PackedLinkUnit(name, cat, devices, loads);

        private static PackedLink Qs(params PackedLinkUnit[] units)
            => new PackedLink(ProcessorLink.QsLinkType, units.Sum(u => u.Devices), units.Sum(u => u.Loads),
                units.Select(u => u.Name).ToArray(), units: units);

        private static PackedLink EmptyQs() => new PackedLink(ProcessorLink.QsLinkType, 0, 0, Array.Empty<string>());

        private static PackedLink Cca(int devices = 5)
            => new PackedLink(ProcessorLink.ClearConnectLinkType, devices, 0, Array.Empty<string>());

        private static PackedLinkUnit ShadeUnit(string name, int motors)
            => new PackedLinkUnit(name, LinkCategory.Shades, motors + 1, motors);

        private static ProcessorGroup Group(PackedLink l1, PackedLink l2, int location = 1)
            => new ProcessorGroup(location, l1, l2);

        private static LinkPackResult Pack(params ProcessorGroup[] groups)
        {
            var flat = groups.SelectMany(g => new[] { g.Link1, g.Link2 }).ToList();
            return new LinkPackResult(flat, groups.Length, 0, groups);
        }

        private static ControlPanelRenderData Proc(string name, int modules = 8)
            => new ControlPanelRenderData(name, "PD8-59F-120", $"{modules}/8",
                Enumerable.Repeat<string?>("LQSE-4A-D", modules).ToList(),
                new[] { "HQP7-2" }, hostsProcessor: true, Roles.ControlPanelDetail);

        private static int[] Numbers(ControlWireLegend legend) => legend.Entries.Select(e => e.Number).ToArray();
        private static ControlWireType[] Types(ControlWireLegend legend) => legend.Entries.Select(e => e.Type).ToArray();

        // ── Presence rules off the pack ──────────────────────────────────────────────────────────────

        [Fact]
        public void ForPack_ShadeFreeWirelessFree_HasOnlyQsAndCat6()
        {
            var legend = ControlWireLegend.ForPack(
                Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs())));

            Assert.Equal(new[] { ControlWireType.QsControlLink, ControlWireType.Cat6 }, Types(legend));
            Assert.Equal(new[] { 1, 2 }, Numbers(legend));
        }

        [Fact]
        public void ForPack_WithShade_AddsShadeRowAtThree()
        {
            var legend = ControlWireLegend.ForPack(
                Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10)), EmptyQs())));

            Assert.Equal(
                new[] { ControlWireType.QsControlLink, ControlWireType.Cat6, ControlWireType.ShadeLink },
                Types(legend));
            Assert.Equal(3, legend.NumberFor(ControlWireType.ShadeLink));
        }

        [Fact]
        public void ForPack_WirelessAddsNoNumberedRow_ClearConnectIsNotACable()
        {
            // A wireless (CC-A) link adds NO numbered row — its cable to the repeater is QS, the RF beyond is a
            // line style. So a wireless-but-shade-free job is just QS=1, CAT6=2.
            var legend = ControlWireLegend.ForPack(
                Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), Cca())));

            Assert.Equal(new[] { ControlWireType.QsControlLink, ControlWireType.Cat6 }, Types(legend));
            Assert.Equal(0, legend.NumberFor(ControlWireType.ClearConnect));   // never rostered
        }

        [Fact]
        public void ForPack_ShadeAndWireless_OnlyQsCat6AndShade()
        {
            // Shade adds row 3; wireless adds nothing — Clear Connect is not a numbered cable.
            var legend = ControlWireLegend.ForPack(
                Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10)), Cca())));

            Assert.Equal(new[]
            {
                ControlWireType.QsControlLink, ControlWireType.Cat6, ControlWireType.ShadeLink,
            }, Types(legend));
            Assert.Equal(new[] { 1, 2, 3 }, Numbers(legend));
            Assert.Equal(0, legend.NumberFor(ControlWireType.ClearConnect));
        }

        [Fact]
        public void ForPack_Null_StillHasTheTwoAlwaysRows()
        {
            var legend = ControlWireLegend.ForPack(null);
            Assert.Equal(new[] { ControlWireType.QsControlLink, ControlWireType.Cat6 }, Types(legend));
        }

        // ── Roster rules ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void BenchedTypes_AreNeverRostered()
        {
            // Even if a caller tries to "use" a benched type, it is not in the canonical roster, so it never gets
            // a numbered row — ClearConnect (RF is a line style, not a cable), PanelControlLink (panel links are
            // drawn as QS) and DaliLoop (DALI is TurboDALI's job).
            var legend = ControlWireLegend.Build(new[]
            {
                ControlWireType.QsControlLink, ControlWireType.ClearConnect,
                ControlWireType.PanelControlLink, ControlWireType.DaliLoop, ControlWireType.Cat6,
            });

            Assert.Equal(new[] { ControlWireType.QsControlLink, ControlWireType.Cat6 }, Types(legend));
            Assert.Equal(0, legend.NumberFor(ControlWireType.ClearConnect));
            Assert.Equal(0, legend.NumberFor(ControlWireType.PanelControlLink));
            Assert.Equal(0, legend.NumberFor(ControlWireType.DaliLoop));
        }

        [Fact]
        public void Build_DenseNumbering_CollapsesDuplicatesAndSkipsAbsent()
        {
            var legend = ControlWireLegend.Build(new[]
            {
                ControlWireType.ShadeLink, ControlWireType.QsControlLink,
                ControlWireType.Cat6, ControlWireType.ShadeLink,   // duplicate shade
            });

            // Canonical order regardless of input order; the duplicate collapses to one row.
            Assert.Equal(
                new[] { ControlWireType.QsControlLink, ControlWireType.Cat6, ControlWireType.ShadeLink },
                Types(legend));
            Assert.Equal(new[] { 1, 2, 3 }, Numbers(legend));
        }

        [Fact]
        public void LabelFor_ShadeLink_IsWiringByOthers_NotLiableForShadeDecisions()
            => Assert.Equal("SHADE WIRING BY OTHERS", ControlWireLegend.LabelFor(ControlWireType.ShadeLink));

        // ── Legend layout planner ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void LegendPlanner_EmitsTitleMarkersAndTheTwoLineStyleKeys()
        {
            var legend = ControlWireLegend.ForPack(
                Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10)), Cca())));
            var drawing = ControlWireLegendPlanner.Build(legend);

            Assert.Equal("WIRE LEGEND", drawing.Title.Text);

            // One marker per NUMBERED cable row (no markers for the line-style keys)...
            Assert.Equal(legend.Entries.Count, drawing.Markers.Count);
            for (int i = 0; i < legend.Entries.Count; i++)
            {
                Assert.Equal(legend.Entries[i].Number, drawing.Markers[i].Number);
                Assert.Equal(legend.Entries[i].Type, drawing.Markers[i].Type);
            }

            // ...and one note per cable row PLUS the two unnumbered line-style key labels.
            Assert.Equal(legend.Entries.Count + 2, drawing.Notes.Count);
            Assert.All(drawing.Notes, n => Assert.False(string.IsNullOrWhiteSpace(n.Text)));
            Assert.Contains(drawing.Notes, n => n.Text == "RF CONNECTION");
            Assert.Contains(drawing.Notes, n => n.Text == "WIRED CONNECTION");

            // Two sample-line keys: exactly one dashed (RF) and one solid (WIRED).
            Assert.Equal(2, drawing.SampleLines.Count);
            Assert.Single(drawing.SampleLines, s => s.Dashed);
            Assert.Single(drawing.SampleLines, s => !s.Dashed);
        }

        // ── Integration: the planner stamps the legend's numbers onto the drawn page markers ───────────

        [Fact]
        public void Planner_StampsLegendNumbersOntoMarkers()
        {
            var pack = Pack(Group(
                Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10)),   // head + a shade on link 1
                Cca()));                                                       // wireless ("RF") on link 2
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = Assert.Single(ControlOneLinePlanner.Build(pack, panels, "TurboControl"));

            // Legend for this job: QS=1, CAT6=2, Shade=3 — assert every stamped marker matches. The wireless leg
            // is QS wire, so it carries a QS (#1) marker; there is NO Clear Connect marker.
            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.QsControlLink), m => Assert.Equal(1, m.Number));
            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.Cat6), m => Assert.Equal(2, m.Number));
            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.ShadeLink), m => Assert.Equal(3, m.Number));

            // The three cable types were actually drawn (so the assertions above weren't vacuous); CC never is.
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.QsControlLink);
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.Cat6);
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.ShadeLink);
            Assert.DoesNotContain(page.Markers, m => m.Type == ControlWireType.ClearConnect);
        }

        [Fact]
        public void Planner_ShadeFreeWirelessFree_OnlyStampsQsAndCat6()
        {
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = Assert.Single(ControlOneLinePlanner.Build(pack, panels, "TurboControl"));

            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.QsControlLink), m => Assert.Equal(1, m.Number));
            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.Cat6), m => Assert.Equal(2, m.Number));
            Assert.DoesNotContain(page.Markers, m => m.Type == ControlWireType.ShadeLink);
            Assert.DoesNotContain(page.Markers, m => m.Type == ControlWireType.ClearConnect);
        }
    }
}
