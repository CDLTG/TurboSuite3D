#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using G = TurboSuite.Zones.OneLine.ControlOneLineGeometry;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>
    /// Lays a solved <see cref="LinkPackResult"/> out into the control one-line drawing(s) — pure geometry
    /// off <see cref="ControlOneLineGeometry"/>, no Revit. Mirrors <c>DmxOneLinePlanner</c>.
    ///
    /// <para><b>Shape (locked 2026-09-24):</b> horizontal. Each <see cref="ProcessorGroup"/> is a bay stacked
    /// down the left; its processor-hosting panel is the head (identified by
    /// <see cref="ControlPanelRenderData.HostsProcessor"/>), drawn once, with both links leaving its right
    /// edge as horizontal rows. Downstream panels/shades hang along each QS row in rule-#5 order (Modules →
    /// Shades → Keypads, natural-name within a category); keypads collapse to one tail stub; a Clear Connect
    /// link draws a wireless stub. A shared HOME NETWORK node feeds CAT6 to every head panel.</para>
    ///
    /// <para><b>Stage 1 (this):</b> one job-wide page — the common case. Pagination into 42×30 pages +
    /// continuation bubbles is stage 2; the return type is already a page list so that is additive.</para>
    /// </summary>
    public static class ControlOnePlannerConstants
    {
        // small note nudges (feet)
        internal const double NoteDx = 2.0 / 12.0;
        internal const double NoteDy = 2.0 / 12.0;
        internal const double WirelessStubLen = 12.0 / 12.0;
    }

    public static class ControlOneLinePlanner
    {
        /// <param name="pack">The pooling-pack result (its <see cref="LinkPackResult.Processors"/> +
        /// per-link <c>Units</c> are consumed — never <c>ProcessorInstance</c>, which drops them).</param>
        /// <param name="panels">Name → power-panel render data (built from PanelResult+BrandConfig). Shade
        /// nodes need no lookup — they render straight off their <see cref="PackedLinkUnit"/> (a shade unit
        /// carries its motor count as <see cref="PackedLinkUnit.Loads"/>).</param>
        /// <param name="systemName">Prefix for the owned view name (e.g. "TurboControl").</param>
        public static IReadOnlyList<ControlOneLineDrawing> Build(
            LinkPackResult pack,
            IReadOnlyDictionary<string, ControlPanelRenderData> panels,
            string systemName)
        {
            var groups = pack?.Processors ?? Array.Empty<ProcessorGroup>();
            if (groups.Count == 0) return Array.Empty<ControlOneLineDrawing>();

            var panelNodes = new List<ControlPanelNode>();
            var shadeNodes = new List<ControlShadeNode>();
            var symbols = new List<ControlSymbolInstance>();
            var wires = new List<ControlWireSegment>();
            var markers = new List<ControlMarker>();
            var notes = new List<ControlNote>();

            var natural = new NaturalStringComparer();

            void Wire(XY a, XY b, bool dashed) => wires.Add(new ControlWireSegment(a, b, dashed));
            void Mark(XY at, ControlWireType t) => markers.Add(new ControlMarker(at, t, NumberFor(t)));
            void Note(XY at, string text, ControlTextAlign align) => notes.Add(new ControlNote(at, text, align));

            void Add120V(XY center, double height)
            {
                double topY = G.Panel.TopEdgeY(center.Y, height);
                var a = new XY(center.X - 18.0 / 12.0, topY);
                var b = a.Offset(0, G.Layout.FeedStubLength);
                Wire(a, b, dashed: false);   // 120 V is power → solid
                Mark(new XY(a.X, (topY + b.Y) / 2.0), ControlWireType.Input120V);
                Note(b.Offset(0, ControlOnePlannerConstants.NoteDy), "120V", ControlTextAlign.Center);
            }

            double colX = G.Layout.ProcessorColumnX;
            double headRightX = colX + G.Panel.Width / 2.0;
            var headCenters = new List<XY>();
            double cursorTopY = 0.0;

            foreach (var group in groups)
            {
                var links = new[] { group.Link1, group.Link2 };

                // ── Identify the head panel (the one hosting the processor) across both links ──
                string? headName = null;
                int headLink = -1;
                for (int i = 0; i < links.Length && headName == null; i++)
                    foreach (var u in links[i].Units)
                        if (panels.TryGetValue(u.Name, out var prd) && prd.HostsProcessor)
                        { headName = u.Name; headLink = i; break; }

                ControlPanelRenderData headRd =
                    headName != null && panels.TryGetValue(headName, out var found)
                        ? found
                        : new ControlPanelRenderData($"P{headCenters.Count + 1}", "", "0/0",
                            Array.Empty<string?>(), new[] { "PROCESSOR" }, hostsProcessor: true,
                            Roles.ControlPanelDetail);

                double headH = PanelHeight(headRd);

                // ── Resolve each link's downstream (rule-#5 ordered), and the bay's tallest node ──
                var downstream = new List<PackedLinkUnit>[2];
                double maxH = headH;
                for (int i = 0; i < 2; i++)
                {
                    downstream[i] = Order(links[i].Units, excludeName: i == headLink ? headName : null, natural);
                    foreach (var u in downstream[i])
                        if (u.Category != LinkCategory.Keypads)
                            maxH = Math.Max(maxH, NodeHeight(u, panels));
                }

                double rowPitch = maxH + G.Layout.LinkRowGap;
                double bayHalf = rowPitch / 2.0 + maxH / 2.0;
                double bayCenterY = cursorTopY - bayHalf;
                var headCenter = new XY(colX, bayCenterY);
                headCenters.Add(headCenter);

                // Head panel + its feed.
                panelNodes.Add(Node(headRd, headCenter));
                Add120V(headCenter, headH);

                double[] rowY = { bayCenterY + rowPitch / 2.0, bayCenterY - rowPitch / 2.0 };
                // QS control riser on the head's right edge, joining both link rows.
                Wire(new XY(headRightX, rowY[0]), new XY(headRightX, rowY[1]), dashed: true);

                for (int i = 0; i < 2; i++)
                {
                    var link = links[i];
                    double y = rowY[i];

                    if (link.IsClearConnect)
                    {
                        var a = new XY(headRightX, y);
                        var b = a.Offset(ControlOnePlannerConstants.WirelessStubLen, 0);
                        Wire(a, b, dashed: true);
                        Mark(new XY((a.X + b.X) / 2.0, y), ControlWireType.ClearConnect);
                        Tick(wires, b, G.Layout.KeypadTickHalf);
                        Note(b.Offset(ControlOnePlannerConstants.NoteDx, ControlOnePlannerConstants.NoteDy),
                            "– WIRELESS KEYPADS", ControlTextAlign.Left);
                        continue;
                    }

                    var located = downstream[i].Where(u => u.Category != LinkCategory.Keypads).ToList();
                    bool hasKeypads = downstream[i].Any(u => u.Category == LinkCategory.Keypads);
                    if (located.Count == 0 && !hasKeypads) continue;   // empty QS link — no row

                    double cursorX = headRightX;
                    Mark(new XY(headRightX + G.Layout.NodeGap * 0.4, y), ControlWireType.QsControlLink);

                    foreach (var u in located)
                    {
                        bool isShade = u.Category == LinkCategory.Shades;
                        double w = isShade ? G.ShadePanel.Width : G.Panel.Width;
                        double cx = cursorX + G.Layout.NodeGap + w / 2.0;
                        Wire(new XY(cursorX, y), new XY(cx - w / 2.0, y), dashed: true);

                        if (isShade)
                        {
                            var c = new XY(cx, y);
                            int motors = u.Loads;   // ShadeSolver emits loads = fill = motors on this QSPS-10PNL
                            shadeNodes.Add(new ControlShadeNode(c, u.Name, ShadeSolver.PanelPartNumber,
                                $"{motors}/{ShadeSolver.ShadesPerPanel}", motors));
                            var m0 = c.Plus(G.ShadePanel.MotorDrop);
                            var m1 = m0.Offset(0, -10.0 / 12.0);
                            Wire(m0, m1, dashed: true);
                            Mark(new XY(m0.X, (m0.Y + m1.Y) / 2.0), ControlWireType.ShadeLink);
                            Note(m1.Offset(ControlOnePlannerConstants.NoteDx, -ControlOnePlannerConstants.NoteDy),
                                $"{motors} MOTORS", ControlTextAlign.Left);
                        }
                        else if (panels.TryGetValue(u.Name, out var prd))
                        {
                            var c = new XY(cx, y);
                            panelNodes.Add(Node(prd, c));
                            Add120V(c, PanelHeight(prd));
                        }
                        cursorX = cx + w / 2.0;
                    }

                    if (hasKeypads)
                    {
                        double kx = cursorX + G.Layout.KeypadTailGap;
                        Wire(new XY(cursorX, y), new XY(kx, y), dashed: true);
                        Tick(wires, new XY(kx, y), G.Layout.KeypadTickHalf);
                        Note(new XY(kx + ControlOnePlannerConstants.NoteDx, y + ControlOnePlannerConstants.NoteDy),
                            "– ALL KEYPADS — REFER TO PLAN", ControlTextAlign.Left);
                        Note(new XY(kx + ControlOnePlannerConstants.NoteDx, y - ControlOnePlannerConstants.NoteDy),
                            "(MAX 10 KEYPADS PER HOMERUN)", ControlTextAlign.Left);
                    }
                }

                cursorTopY = bayCenterY - bayHalf - G.Layout.BayGap;
            }

            // ── HOME NETWORK node + CAT6 to each head panel ──
            double trunkX = G.Layout.Cat6TrunkX;
            var netCenter = new XY(trunkX, headCenters[0].Y + G.Panel.Height(0, 0));
            symbols.Add(new ControlSymbolInstance(ControlSymbolKind.HomeNetwork, netCenter,
                new Dictionary<string, string>()));
            double trunkTop = netCenter.Y - G.Layout.HomeNetworkHeight / 2.0;
            double trunkBot = headCenters[headCenters.Count - 1].Y;
            Wire(new XY(trunkX, trunkTop), new XY(trunkX, trunkBot), dashed: true);
            Mark(new XY(trunkX, (trunkTop + headCenters[0].Y) / 2.0), ControlWireType.Cat6);
            foreach (var hc in headCenters)
                Wire(new XY(trunkX, hc.Y), new XY(colX - G.Panel.Width / 2.0, hc.Y), dashed: true);

            var page = new ControlOneLineDrawing(1, 1, panelNodes, shadeNodes, symbols, wires, markers, notes,
                Array.Empty<ControlContinuation>());
            return new[] { page };
        }

        /// <summary>Rule #5: category order (Modules → Shades → Interface → Keypads), natural-name within a
        /// category. The synthetic "Keypads" unit sorts last. The head panel is dropped via
        /// <paramref name="excludeName"/> so it is not re-drawn as a downstream node.</summary>
        internal static List<PackedLinkUnit> Order(IReadOnlyList<PackedLinkUnit> units, string? excludeName,
            NaturalStringComparer natural)
            => units
                .Where(u => excludeName == null || !string.Equals(u.Name, excludeName, StringComparison.Ordinal))
                .OrderBy(u => CategoryRank(u.Category))
                .ThenBy(u => u.Name, natural)
                .ToList();

        private static int CategoryRank(LinkCategory c) => c switch
        {
            LinkCategory.Modules => 0,
            LinkCategory.Shades => 1,
            LinkCategory.Interface => 2,
            LinkCategory.Keypads => 3,
            _ => 4,
        };

        /// <summary>Canonical wire-type number (placeholder until <c>ControlWireLegend</c>, #5, owns the
        /// per-job dense numbering). Matches the mockup legend.</summary>
        internal static int NumberFor(ControlWireType t) => t switch
        {
            ControlWireType.QsControlLink => 1,
            ControlWireType.PanelControlLink => 2,
            ControlWireType.ClearConnect => 3,
            ControlWireType.Input120V => 4,
            ControlWireType.ShadeLink => 5,
            ControlWireType.Cat6 => 6,
            ControlWireType.DaliLoop => 7,
            _ => 0,
        };

        private static double PanelHeight(ControlPanelRenderData rd)
            => G.Panel.Height(rd.ModuleTiles.Count, rd.LvSlots.Count);

        private static double NodeHeight(PackedLinkUnit u,
            IReadOnlyDictionary<string, ControlPanelRenderData> panels)
            => u.Category != LinkCategory.Shades && panels.TryGetValue(u.Name, out var rd)
                ? PanelHeight(rd) : G.ShadePanel.Height;

        private static ControlPanelNode Node(ControlPanelRenderData rd, XY center)
            => new ControlPanelNode(center, rd.Name, rd.PartNumber, rd.FillText, rd.ModuleTiles,
                rd.LvSlots, rd.HostsProcessor, rd.EnclosureRole);

        private static void Tick(List<ControlWireSegment> wires, XY at, double half)
            => wires.Add(new ControlWireSegment(at.Offset(0, -half), at.Offset(0, half), dashed: false));
    }
}
