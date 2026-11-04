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
    //  are a tail stub only where they landed, Clear Connect draws a wireless stub, each head draws live +
    //  spare CAT6 stubs to the home network, and a spare CAT6 daisy chain ties consecutive heads.
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

        /// <summary>F4 — a CC-A link carrying repeaters chains one <c>ControlRepeaterDetail</c> stamp per
        /// repeater, each labeled with the job's repeater catalog, and still hangs the WIRELESS KEYPADS stub
        /// at the tail (the per-keypad fan is deferred). The stamps sit left→right at the repeater
        /// center-to-center pitch, non-overlapping.</summary>
        [Fact]
        public void ClearConnectLinkChainsARepeaterStampPerRepeater()
        {
            var pack = Pack(Group(
                Qs(U("1-A", LinkCategory.Modules, 8)),
                new PackedLink(ProcessorLink.ClearConnectLinkType, 3, 0, Array.Empty<string>(), repeaters: 3)));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = Assert.Single(
                ControlOneLinePlanner.Build(pack, panels, "TurboControl", "HQR-REP-120"));

            Assert.Equal(3, page.Repeaters.Count);
            Assert.All(page.Repeaters, r => Assert.Equal("HQR-REP-120", r.PartNumber));

            var xs = page.Repeaters.Select(r => r.Center.X).ToList();
            Assert.Equal(xs.OrderBy(x => x).ToList(), xs);                 // left→right
            // First stamp at the head→column-1 c-c off the head center (15'-0"), then the inter-node c-c floor
            // (12'-0") between fan-less repeaters — a repeater sits exactly where a panel would on the link.
            Assert.Equal(
                ControlOneLineGeometry.Layout.ProcessorColumnX + ControlOneLineGeometry.Layout.HeadColumnCenterToCenter,
                xs[0], 3);
            for (int i = 1; i < xs.Count; i++)
                Assert.Equal(ControlOneLineGeometry.Layout.PanelCenterToCenter, xs[i] - xs[i - 1], 3);

            Assert.Contains(page.Notes, n => n.Text.Contains("WIRELESS KEYPADS"));
        }

        /// <summary>A wireless link with no repeater modelled (a 0/4 bar) draws no stamp, but still the
        /// WIRELESS KEYPADS stub — the wireless devices have to live somewhere.</summary>
        [Fact]
        public void ClearConnectLinkWithNoRepeaterDrawsTheStubWithNoStamp()
        {
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), Cca(devices: 2)));   // repeaters: 0
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Empty(page.Repeaters);
            Assert.Contains(page.Notes, n => n.Text.Contains("WIRELESS KEYPADS"));
        }

        /// <summary>F4 — when a CC-A link carries per-repeater RECORDS, each stamp fans ITS OWN keypads
        /// (rising from the stamp) and the aggregate WIRELESS KEYPADS stub is gone. Two repeaters carrying
        /// 2 + 1 keypads → two stamps, three fan rows, and a wireless glyph at each tap + row.</summary>
        [Fact]
        public void ClearConnectRepeatersFanTheirOwnKeypads()
        {
            KeypadRecord Kp(string id) => new KeypadRecord(id, "ENTRY", "Palladiom", location: 1);
            var r1 = new RepeaterRecord("1-REP1", 1, new[] { Kp("K1"), Kp("K2") });
            var r2 = new RepeaterRecord("1-REP2", 1, new[] { Kp("K3") });
            var cca = new PackedLink(ProcessorLink.ClearConnectLinkType, 5, 0, Array.Empty<string>(),
                repeaters: 2, repeaterRecords: new[] { r1, r2 });
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), cca));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = Assert.Single(
                ControlOneLinePlanner.Build(pack, panels, "TurboControl", "HQR-REP-120"));

            Assert.Equal(2, page.Repeaters.Count);                                        // two stamps
            Assert.DoesNotContain(page.Notes, n => n.Text.Contains("WIRELESS KEYPADS"));  // stub replaced by fans
            Assert.Equal(3, page.Notes.Count(n => n.Text.StartsWith("[K")));              // one fan row per keypad
            // Wireless glyphs: one at each stamp's tap + one per keypad row (3 + 2 across the two fans).
            Assert.Equal(5, page.Glyphs.Count);
            Assert.All(page.Glyphs, g => Assert.Equal(Roles.ControlWirelessGlyph, g.Role));
        }

        /// <summary>Unified c-c spacing (the beaver plan): a node's own fan drives ONLY its right gap. A
        /// repeater carrying a wide keypad fan widens the gap to the next stamp to exactly
        /// <c>FanWidth + FanGapPad</c> (past the floor), while a fan-less neighbor's right gap sits at the
        /// <c>PanelCenterToCenter</c> floor. Three stamps: a wide first fan, then two fan-less.</summary>
        [Fact]
        public void WideFanWidensOnlyItsOwnRightGap_FanlessNeighborStaysAtTheFloor()
        {
            var wideKp = new KeypadRecord("K1", "GRAND BEDROOM SITTING AREA", "seeTouch Tabletop", location: 1);
            var r1 = new RepeaterRecord("1-REP1", 1, new[] { wideKp });   // wide fan → widens its right gap
            var r2 = new RepeaterRecord("1-REP2", 1);                     // no keypads → no fan
            var r3 = new RepeaterRecord("1-REP3", 1);                     // no keypads → no fan (terminal)
            var cca = new PackedLink(ProcessorLink.ClearConnectLinkType, 99, 0, Array.Empty<string>(),
                repeaters: 3, repeaterRecords: new[] { r1, r2, r3 });
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), cca));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = Assert.Single(
                ControlOneLinePlanner.Build(pack, panels, "TurboControl", "HQR-REP-120"));

            var xs = page.Repeaters.Select(r => r.Center.X).OrderBy(x => x).ToList();
            Assert.Equal(3, xs.Count);

            double floor = ControlOneLineGeometry.Layout.PanelCenterToCenter;
            string wideLabel = ControlListLayout.KeypadRowLabel(wideKp);
            double expectedWideGap = ControlOneLineGeometry.Layout.KeypadListTextDx
                + wideLabel.Length * ControlOneLineGeometry.Layout.KeypadListCharWidth
                + ControlOneLineGeometry.Layout.FanGapPad;

            Assert.True(expectedWideGap > floor, "the test's wide label must actually exceed the floor");
            Assert.Equal(expectedWideGap, xs[1] - xs[0], 3);   // R1's wide fan drove its own right gap
            Assert.Equal(floor, xs[2] - xs[1], 3);             // R2 is fan-less → its right gap is the floor
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
        /// its OWN Ethernet-to-Home-Network stubs (live + spare, each a CAT6 marker + a labeled note) out its left
        /// edge — there is no shared switch node (per the Lutron reference sheet).</summary>
        [Fact]
        public void EachProcessorDrawsItsOwnEthernetStubs()
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
            // Two Ethernet stubs per head + the one daisy-chain link between them, each with a CAT6 marker.
            Assert.Equal(5, page.Markers.Count(m => m.Type == ControlWireType.Cat6));
            Assert.Equal(2, page.Notes.Count(n => n.Text == LiveEthernetLabel));
            Assert.Equal(2, page.Notes.Count(n => n.Text == SpareEthernetLabel));
        }

        /// <summary>The spare is a full second stub, not just a label: left edge out, its marker in the shared
        /// marker column, and one pitch above the live stub, which stays exactly where it was.</summary>
        [Fact]
        public void SpareEthernetStubSitsOnePitchAboveTheLiveStub()
        {
            var pack = Pack(Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            var head = Assert.Single(page.Panels, p => p.HostsProcessor);
            double headBottomY = HeadBottomY(head);
            double headLeftX = head.Center.X - ControlOneLineGeometry.Panel.Width / 2.0;
            double liveY = headBottomY + ControlOneLineGeometry.Layout.EthernetStubAboveOrigin;
            double spareY = liveY + ControlOneLineGeometry.Layout.EthernetSparePitch;

            foreach (double y in new[] { liveY, spareY })
            {
                Assert.Contains(page.Wires, w => Near(w.Start, headLeftX, y)
                    && Near(w.End, headLeftX - ControlOneLineGeometry.Layout.EthernetStubLen, y));
                Assert.Contains(page.Markers, m => m.Type == ControlWireType.Cat6
                    && Near(m.Position, headLeftX - ControlOneLineGeometry.Layout.MarkerExitOffset, y));
            }

            var live = Assert.Single(page.Notes, n => n.Text == LiveEthernetLabel);
            var spare = Assert.Single(page.Notes, n => n.Text == SpareEthernetLabel);
            Assert.Equal(headBottomY + ControlOneLineGeometry.Layout.EthernetLabelDy, live.Position.Y, 3);
            Assert.Equal(live.Position.Y + ControlOneLineGeometry.Layout.EthernetSparePitch, spare.Position.Y, 3);
            Assert.Equal(live.Position.X, spare.Position.X, 3);
        }

        /// <summary>The head's two Ethernet stubs line up with its bottom two QS exits across the enclosure.</summary>
        [Fact]
        public void EthernetStubsAlignWithTheBottomTwoQsExits()
        {
            var pack = Pack(Group(
                Qs(U("1-A", LinkCategory.Modules, 8), U("1-B", LinkCategory.Modules, 6)),
                Qs(U("1-C", LinkCategory.Modules, 6))));
            var panels = new Dictionary<string, ControlPanelRenderData>
            {
                ["1-A"] = Proc("1-A"), ["1-B"] = Dimmer("1-B"), ["1-C"] = Dimmer("1-C"),
            };

            var page = BuildOne(pack, panels);

            var head = Assert.Single(page.Panels, p => p.HostsProcessor);
            double headRightX = head.Center.X + ControlOneLineGeometry.Panel.Width / 2.0;
            double headLeftX = head.Center.X - ControlOneLineGeometry.Panel.Width / 2.0;
            double liveY = HeadBottomY(head) + ControlOneLineGeometry.Layout.EthernetStubAboveOrigin;
            double spareY = liveY + ControlOneLineGeometry.Layout.EthernetSparePitch;

            foreach (double y in new[] { liveY, spareY })
            {
                Assert.Contains(page.Wires, w => Near(w.Start, headLeftX, y));    // Ethernet stub, left edge
                Assert.Contains(page.Wires, w => Near(w.Start, headRightX, y));   // QS exit, right edge
            }
        }

        /// <summary>One panel, no chain — even an LV21, whose two processors are jumpered inside the enclosure
        /// with no wire to pull. It also gets one live + one spare stub per PANEL, not per processor.</summary>
        [Fact]
        public void LoneHeadDrawsNoDaisyChain_EvenAnLv21WithTwoProcessors()
        {
            var pack = Pack(Lv21ThreeRows("1-A", "1-B", "1-C", "1-D"));
            var panels = new Dictionary<string, ControlPanelRenderData>
            {
                ["1-A"] = Lv21("1-A"), ["1-B"] = Dimmer("1-B"), ["1-C"] = Dimmer("1-C"), ["1-D"] = Dimmer("1-D"),
            };

            var page = BuildOne(pack, panels);

            Assert.DoesNotContain(page.Notes, n => n.Text.Contains("DAISY CHAIN"));
            Assert.Equal(2, page.Markers.Count(m => m.Type == ControlWireType.Cat6));
            Assert.Single(page.Notes, n => n.Text == LiveEthernetLabel);
            Assert.Single(page.Notes, n => n.Text == SpareEthernetLabel);
        }

        /// <summary>Two heads on one sheet: a straight CAT6 run on the centerline from the upper head's bottom to
        /// the lower head's top, with its marker at the wire's midpoint and its note beside it, left of the wire
        /// (the QS lanes drop through the right).</summary>
        [Fact]
        public void HeadsOnOneSheetAreTiedByAStraightDaisyChainRun()
        {
            var pack = Pack(
                Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs(), location: 1),
                Group(Qs(U("2-A", LinkCategory.Modules, 8)), EmptyQs(), location: 2));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A"), ["2-A"] = Proc("2-A") };

            var page = BuildOne(pack, panels);

            var upper = Assert.Single(page.Panels, p => p.Name == "1-A");
            var lower = Assert.Single(page.Panels, p => p.Name == "2-A");
            double fromY = HeadBottomY(upper);
            double toY = HeadTopY(lower);
            Assert.True(fromY > toY);

            Assert.Contains(page.Wires, w => Near(w.Start, upper.Center.X, fromY) && Near(w.End, upper.Center.X, toY));
            double midY = (fromY + toY) / 2.0;
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.Cat6 && Near(m.Position, upper.Center.X, midY));

            var label = Assert.Single(page.Notes, n => n.Text.Contains("DAISY CHAIN"));
            Assert.Equal("SPARE ETHERNET\rDAISY CHAIN", label.Text);
            Assert.Equal(ControlTextAlign.Right, label.Align);
            Assert.Equal(ControlVerticalAlign.Middle, label.VAlign);
            Assert.True(Near(label.Position, upper.Center.X - ControlOneLineGeometry.Layout.DaisyChainLabelDx, midY));
        }

        /// <summary>The chain across a sheet break: a stub + label on each side naming the head at the other end.
        /// Five heads on three sheets exercise every case — a sheet that only sends, one that receives, chains
        /// and sends, and one that only receives — and the chain stays open (no wrap from the last to the first).</summary>
        [Fact]
        public void DaisyChainContinuesAcrossSheetsAsNamedStubs()
        {
            ProcessorGroup Pd8(string h, string d1, string d2)
                => Group(Qs(U(h, LinkCategory.Modules, 8), U(d1, LinkCategory.Modules, 6)),
                         Qs(U(d2, LinkCategory.Modules, 6)));
            var names = new[] { "A", "B", "C", "D", "E" };
            var pack = Pack(names.Select(n => Pd8(n, n + "2", n + "3")).ToArray());
            var panels = new Dictionary<string, ControlPanelRenderData>();
            foreach (var n in names)
            {
                panels[n] = Proc(n);
                panels[n + "2"] = Dimmer(n + "2");
                panels[n + "3"] = Dimmer(n + "3");
            }

            var pages = ControlOneLinePlanner.Build(pack, panels, "TurboControl");

            Assert.Equal(3, pages.Count);
            Assert.Equal(new[] { "SPARE ETHERNET\rDAISY CHAIN", "SPARE ETHERNET\rDAISY CHAIN TO C" }, ChainLabels(pages[0]));
            Assert.Equal(new[] { "SPARE ETHERNET\rDAISY CHAIN", "SPARE ETHERNET\rDAISY CHAIN FROM B", "SPARE ETHERNET\rDAISY CHAIN TO E" }, ChainLabels(pages[1]));
            Assert.Equal(new[] { "SPARE ETHERNET\rDAISY CHAIN FROM D" }, ChainLabels(pages[2]));

            // Page 1's outgoing stub drops off B; page 3's incoming stub rises off E. Each stub is the fixed length,
            // ending in open space, with its marker by the head and a center-aligned note in line with the wire,
            // just past the free end: hanging below a stub that drops, sitting above one that rises.
            var b = Assert.Single(pages[0].Panels, p => p.Name == "B");
            double bBottom = HeadBottomY(b);
            double bEnd = bBottom - ControlOneLineGeometry.Layout.DaisyChainStubLen;
            Assert.Contains(pages[0].Wires, w => Near(w.Start, b.Center.X, bBottom) && Near(w.End, b.Center.X, bEnd));
            Assert.Contains(pages[0].Markers, m => m.Type == ControlWireType.Cat6
                && Near(m.Position, b.Center.X, bBottom - ControlOneLineGeometry.Layout.MarkerExitOffset));
            var outgoing = Assert.Single(pages[0].Notes, n => n.Text.EndsWith(" TO C"));
            Assert.Equal(ControlTextAlign.Center, outgoing.Align);
            Assert.Equal(ControlVerticalAlign.Top, outgoing.VAlign);
            Assert.True(Near(outgoing.Position, b.Center.X, bEnd - ControlOneLineGeometry.Layout.DaisyChainStubNoteGap));

            var e = Assert.Single(pages[2].Panels, p => p.Name == "E");
            double eTop = HeadTopY(e);
            double eEnd = eTop + ControlOneLineGeometry.Layout.DaisyChainStubLen;
            Assert.Contains(pages[2].Wires, w => Near(w.Start, e.Center.X, eTop) && Near(w.End, e.Center.X, eEnd));
            Assert.Contains(pages[2].Markers, m => m.Type == ControlWireType.Cat6
                && Near(m.Position, e.Center.X, eTop + ControlOneLineGeometry.Layout.MarkerExitOffset));
            var incoming = Assert.Single(pages[2].Notes, n => n.Text.EndsWith(" FROM D"));
            Assert.Equal(ControlTextAlign.Center, incoming.Align);
            Assert.Equal(ControlVerticalAlign.Bottom, incoming.VAlign);
            Assert.True(Near(incoming.Position, e.Center.X, eEnd + ControlOneLineGeometry.Layout.DaisyChainStubNoteGap));

            // CAT6 markers: 2 per head + 1 per chain run. Page 1 = 2 heads + run + stub; page 2 = 2 heads + run +
            // two stubs; page 3 = 1 head + stub.
            Assert.Equal(6, pages[0].Markers.Count(m => m.Type == ControlWireType.Cat6));
            Assert.Equal(7, pages[1].Markers.Count(m => m.Type == ControlWireType.Cat6));
            Assert.Equal(3, pages[2].Markers.Count(m => m.Type == ControlWireType.Cat6));
        }

        /// <summary>The chain does not change the wire legend's roster: it is the same CAT6 cable, one number.</summary>
        [Fact]
        public void DaisyChainAndSpareStubsShareTheCat6LegendNumber()
        {
            var pack = Pack(
                Group(Qs(U("1-A", LinkCategory.Modules, 8)), EmptyQs(), location: 1),
                Group(Qs(U("2-A", LinkCategory.Modules, 8)), EmptyQs(), location: 2));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A"), ["2-A"] = Proc("2-A") };

            var page = BuildOne(pack, panels);

            int cat6 = ControlWireLegend.ForPack(pack).NumberFor(ControlWireType.Cat6);
            Assert.All(page.Markers.Where(m => m.Type == ControlWireType.Cat6), m => Assert.Equal(cat6, m.Number));
        }

        private const string LiveEthernetLabel = "ETHERNET LINK TO\rHOME NETWORK";
        private const string SpareEthernetLabel = "SPARE ETHERNET LINK TO\rHOME NETWORK";

        private static double NodeHeight(ControlPanelNode p)
            => ControlOneLineGeometry.Panel.Height(p.ModuleTiles.Count, p.LvSlots.Count);
        private static double HeadBottomY(ControlPanelNode p) => p.Center.Y - NodeHeight(p) / 2.0;
        private static double HeadTopY(ControlPanelNode p) => p.Center.Y + NodeHeight(p) / 2.0;
        private static bool Near(XY a, double x, double y)
            => System.Math.Abs(a.X - x) < 1e-6 && System.Math.Abs(a.Y - y) < 1e-6;
        private static string[] ChainLabels(ControlOneLineDrawing page)
            => page.Notes.Where(n => n.Text.Contains("DAISY CHAIN")).Select(n => n.Text)
                .OrderBy(t => t, System.StringComparer.Ordinal).ToArray();

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
        /// and each sheet's head draws its own home-network stubs; only the daisy chain continues across the break.</summary>
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
            // Each sheet's head draws its own live + spare Ethernet stubs.
            Assert.Equal(2, pages[0].Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
            Assert.Equal(2, pages[1].Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
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
            // Ethernet stubs are per head, live + spare: page 1 has two heads (four stubs), page 2 one (two).
            Assert.Equal(4, pages[0].Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
            Assert.Equal(2, pages[1].Notes.Count(n => n.Text.Contains("ETHERNET LINK")));
        }
    }
}
