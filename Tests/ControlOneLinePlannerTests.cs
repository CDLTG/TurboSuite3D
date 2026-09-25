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

        private static PackedLink Cca(int devices = 5)
            => new PackedLink(ProcessorLink.ClearConnectLinkType, devices, 0, Array.Empty<string>());

        private static ProcessorGroup Group(PackedLink l1, PackedLink l2, int location = 1)
            => new ProcessorGroup(location, l1, l2);

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

        /// <summary>The text-block case: Link 1 QS carries modules + shade + keypads; Link 2 is Clear Connect.
        /// The keypad stub lands on the QS link, the wireless stub on the CC leg, and nothing draws on CC.</summary>
        [Fact]
        public void ClearConnectLinkDrawsWirelessStubAndNoNodes()
        {
            var pack = Pack(Group(
                Qs(U("1-A", LinkCategory.Modules, 8), ShadeUnit("1-D", 10), U("Keypads", LinkCategory.Keypads, 10)),
                Cca()));
            var panels = new Dictionary<string, ControlPanelRenderData> { ["1-A"] = Proc("1-A") };

            var page = BuildOne(pack, panels);

            Assert.Contains(page.Notes, n => n.Text.Contains("WIRELESS KEYPADS"));
            Assert.Contains(page.Notes, n => n.Text.Contains("ALL KEYPADS"));
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.ClearConnect);
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

        /// <summary>Multi-processor: each bay's head sits at the left column on its own row, and one shared
        /// HOME NETWORK node feeds CAT6 to every processor.</summary>
        [Fact]
        public void SharedHomeNetworkFeedsCat6ToEachProcessor()
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
            Assert.Single(page.Symbols, s => s.Kind == ControlSymbolKind.HomeNetwork);
            Assert.Contains(page.Markers, m => m.Type == ControlWireType.Cat6);
        }
    }
}
