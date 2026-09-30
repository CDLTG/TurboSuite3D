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
    /// <see cref="ControlPanelRenderData.HostsProcessor"/>), drawn once and BOTTOM-ALIGNED to its first (top)
    /// row's baseline in the left column. Its QS links leave the head's RIGHT edge and <b>dogleg</b> through the
    /// head→column-1 gap as a fan of separate lanes — one per link, nested crossing-free (deepest row = leftmost
    /// lane + lowest exit) — dropping to each row's spine (Screenshot_593 / 582). Downstream panels/shades hang
    /// along each QS row in rule-#5 order (Modules → Shades → Keypads, natural-name within a category); keypads
    /// collapse to one tail stub; a Clear Connect link doglegs to its row and draws a wireless stub. A shared
    /// HOME NETWORK node feeds CAT6 to each head's bottom-left.</para>
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
            double headLeftX = colX - G.Panel.Width / 2.0;
            double col1LeftX = headRightX + G.Layout.HeadColumnGap;   // column-1 panels start here; the gap holds the fan
            var headCat6Taps = new List<XY>();                         // bottom-left CAT6 tap per head

            // Rows are LINKS, laid out top-down at a uniform origin-to-origin pitch: every processor's two links
            // are flattened into consecutive rows (proc1·link1, proc1·link2, proc2·link1, …), so 2× PD8 and 1×
            // LV21 both fill 4 rows identically. Row 0 center sits at Y=0; each row below is RowPitch lower.
            // (Pagination past 4 rows / >2 groups is a later step — for now all rows lay onto one page.)
            int rowBase = 0;   // first row index this group's two links occupy
            double RowCenterY(int r) => -r * G.Layout.RowPitch;

            // Walk the groups, MERGING consecutive groups that share a processor panel name into one physical
            // enclosure: an LV21 = two processor compartments = two same-named groups = ONE drawn head with up to
            // 4 link rows (Screenshot_593). Groups with no name (tests / legacy) each stand alone, and the head is
            // then found by scanning link units (the PD8 that is also a Modules unit). This is what fixes the
            // module-less LV21 drawing as two fallback P# boxes — it is no unit on any link, so only the name sees it.
            int headOrdinal = 0;
            int gi = 0;
            while (gi < groups.Count)
            {
                string? encName = groups[gi].ProcessorPanelName;
                var encGroups = new List<ProcessorGroup> { groups[gi] };
                gi++;
                while (!string.IsNullOrEmpty(encName) && gi < groups.Count
                       && string.Equals(groups[gi].ProcessorPanelName, encName, StringComparison.Ordinal))
                {
                    encGroups.Add(groups[gi]);
                    gi++;
                }
                headOrdinal++;

                // The enclosure's links flattened in packer order (link1 then link2, per group). Rows are assigned
                // AFTER liveness, below — a completely empty link (0 devices/loads, e.g. the spare QS link on a
                // processor added only to host a Clear Connect link) consumes NO row, so live links stack
                // consecutively with no blank band where a dead link would have sat.
                var plans = new List<LinkPlan>();
                foreach (var g in encGroups)
                {
                    plans.Add(new LinkPlan { Link = g.Link1 });
                    plans.Add(new LinkPlan { Link = g.Link2 });
                }

                // ── Identify the head: by enclosure name first (this is the ONLY way to see a module-less LV21),
                //    else by scanning the links' units for a processor-hosting panel (a PD8 that is also a Modules
                //    unit — the case the tests build without a name), else a stub. ──
                ControlPanelRenderData? headRd = null;
                if (!string.IsNullOrEmpty(encName) && panels.TryGetValue(encName, out var byName) && byName.HostsProcessor)
                    headRd = byName;
                if (headRd == null)
                    foreach (var pl in plans)
                    {
                        foreach (var u in pl.Link.Units)
                            if (panels.TryGetValue(u.Name, out var prd) && prd.HostsProcessor)
                            { headRd = prd; encName = u.Name; break; }
                        if (headRd != null) break;
                    }
                headRd ??= new ControlPanelRenderData($"P{headOrdinal}", "", "0/0",
                    Array.Empty<string?>(), new[] { "PROCESSOR" }, hostsProcessor: true, Roles.ControlPanelDetail);

                double headH = PanelHeight(headRd);

                // ── Resolve each link's downstream (rule-#5 ordered, head excluded) + which links are LIVE ──
                foreach (var pl in plans)
                {
                    if (pl.Link.IsClearConnect) { pl.IsCC = true; pl.Live = true; continue; }
                    var ordered = Order(pl.Link.Units, excludeName: encName, natural);
                    pl.Located = ordered.Where(u => u.Category != LinkCategory.Keypads).ToList();
                    pl.HasKeypads = ordered.Any(u => u.Category == LinkCategory.Keypads);
                    pl.Live = pl.Located.Count > 0 || pl.HasKeypads;   // empty QS link → dropped, no row
                }

                // ── Compact: assign consecutive rows to the LIVE links only, in flattened order (dead links get
                //    none, so no 28' blank band remains). The head bottom-aligns to the first live row's baseline. ──
                int rowStart = rowBase;
                var live = plans.Where(pl => pl.Live).ToList();
                for (int idx = 0; idx < live.Count; idx++)
                {
                    live[idx].Row = rowStart + idx;
                    live[idx].Y = RowCenterY(rowStart + idx);
                }
                rowBase = rowStart + System.Math.Max(1, live.Count);   // reserve ≥1 row even for a head with no live links

                // Head is BOTTOM-ALIGNED to its first (top) row's baseline, in the left column (Screenshot_593).
                // headBottomY is the head's ORIGIN Y (center-bottom of the artwork) — the datum link exits anchor to.
                double headBottomY = RowCenterY(rowStart) - G.Panel.PowerPanelHeight / 2.0;
                var headCenter = new XY(colX, headBottomY + headH / 2.0);

                panelNodes.Add(Node(headRd, headCenter));
                Add120V(headCenter, headH);   // 120 V feed on top
                // CAT6 enters bottom-left, IN-LINE with the bottom (deepest-row) link exit on the right edge.
                headCat6Taps.Add(new XY(headLeftX, headBottomY + G.Layout.LastRowExitAboveOrigin));

                // ── Fan the live links out of the head's right edge as separate, crossing-free lanes ──
                // Live links in row order (shallowest first). Exits anchor to the head ORIGIN: the DEEPEST (last)
                // row sits lowest at LastRowExitAboveOrigin, each shallower row one HeadExitPitch higher — so exit
                // Y and lane both follow deepest-first (deepest → leftmost lane + lowest exit), crossing-free.
                int n = live.Count;
                for (int k = 0; k < n; k++)
                {
                    int rowsAboveLast = (n - 1) - k;   // 0 for the deepest (last) live row
                    live[k].LaneX = G.Layout.LaneX(headRightX, col1LeftX, laneFromLeft: rowsAboveLast, laneCount: n);
                    live[k].ExitY = headBottomY + G.Layout.LastRowExitAboveOrigin + rowsAboveLast * G.Layout.HeadExitPitch;
                }

                foreach (var pl in live)
                {
                    double y = pl.Y;
                    double lx = pl.LaneX;
                    double ey = pl.ExitY;

                    // Dogleg out of the head: right edge → lane (the bend X, in the gap). The drop to the row happens
                    // per-branch below (a QS link drops to its spine; an RF link drops to the row center).
                    Wire(new XY(headRightX, ey), new XY(lx, ey), dashed: true);   // exit horizontal

                    if (pl.IsCC)
                    {
                        Wire(new XY(lx, ey), new XY(lx, y), dashed: true);        // drop to the row
                        var b = new XY(lx + ControlOnePlannerConstants.WirelessStubLen, y);
                        Wire(new XY(lx, y), b, dashed: true);
                        Mark(new XY((lx + b.X) / 2.0, y), ControlWireType.ClearConnect);
                        Tick(wires, b, G.Layout.KeypadTickHalf);
                        Note(b.Offset(ControlOnePlannerConstants.NoteDx, ControlOnePlannerConstants.NoteDy),
                            "– WIRELESS KEYPADS", ControlTextAlign.Left);
                        continue;
                    }

                    // The QS daisy (spine) runs LinkSpineDropFt BELOW the shared bottom-origin baseline; every
                    // child node — dimmer AND shade, both bottom-aligned — taps it with a caret whose apex sits ON
                    // its origin (Screenshot_582/591/592). The LAST node on a link that ends here (no keypad tail
                    // after it) draws only the LEFT half of its caret, so no half-caret dangles past it.
                    double panelBottomY = y - G.Panel.PowerPanelHeight / 2.0;
                    double spineY = panelBottomY - G.Layout.LinkSpineDropFt;

                    // Drop the lane to the spine (bend at lane X), then the spine runs right through the gap into
                    // column 1.
                    Wire(new XY(lx, ey), new XY(lx, spineY), dashed: true);
                    Mark(new XY(lx, (ey + spineY) / 2.0), ControlWireType.QsControlLink);

                    double cursorX = col1LeftX - G.Layout.NodeGap;   // so the first node lands at col1LeftX + w/2
                    double spineFromX = lx;                          // spine starts at the lane drop

                    // Caret from the spine up to a node's bottom origin at cxAt. leftHalfOnly = a terminal node
                    // with nothing after it: draw spine→leftFoot→apex and stop, so no right half dangles.
                    void Caret(double cxAt, bool leftHalfOnly)
                    {
                        double leftFoot = cxAt - G.Layout.CaretHalfWidth;
                        Wire(new XY(spineFromX, spineY), new XY(leftFoot, spineY), dashed: true);
                        Wire(new XY(leftFoot, spineY), new XY(cxAt, panelBottomY), dashed: true);
                        if (leftHalfOnly) { spineFromX = cxAt; return; }
                        double rightFoot = cxAt + G.Layout.CaretHalfWidth;
                        Wire(new XY(cxAt, panelBottomY), new XY(rightFoot, spineY), dashed: true);
                        spineFromX = rightFoot;
                    }

                    var loc = pl.Located;
                    for (int m = 0; m < loc.Count; m++)
                    {
                        var u = loc[m];
                        bool isShade = u.Category == LinkCategory.Shades;
                        double w = isShade ? G.ShadePanel.Width : G.Panel.Width;
                        double cx = cursorX + G.Layout.NodeGap + w / 2.0;
                        Caret(cx, leftHalfOnly: m == loc.Count - 1 && !pl.HasKeypads);

                        if (isShade)
                        {
                            // Bottom-aligned with the power panels (origin on the shared baseline); the motor leg
                            // rises out the TOP as a single "n MOTORS" stub — no shade symbols drawn.
                            var c = new XY(cx, panelBottomY + G.ShadePanel.Height / 2.0);
                            int motors = u.Loads;   // ShadeSolver emits loads = fill = motors on this QSPS-10PNL
                            shadeNodes.Add(new ControlShadeNode(c, u.Name, ShadeSolver.PanelPartNumber,
                                $"{motors}/{ShadeSolver.ShadesPerPanel}", motors));
                            Add120V(c, G.ShadePanel.Height);   // same feed as a power panel (shared helper → stays in sync)
                            var m0 = c.Plus(G.ShadePanel.MotorTap);
                            var m1 = m0.Offset(0, G.Layout.FeedStubLength);
                            Wire(m0, m1, dashed: true);
                            Mark(new XY(m0.X, (m0.Y + m1.Y) / 2.0), ControlWireType.ShadeLink);
                            Note(m1.Offset(ControlOnePlannerConstants.NoteDx, ControlOnePlannerConstants.NoteDy),
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

                    if (pl.HasKeypads)
                    {
                        double kx = cursorX + G.Layout.KeypadTailGap;
                        Wire(new XY(spineFromX, spineY), new XY(kx, spineY), dashed: true);
                        Tick(wires, new XY(kx, spineY), G.Layout.KeypadTickHalf);
                        Note(new XY(kx + ControlOnePlannerConstants.NoteDx, spineY + ControlOnePlannerConstants.NoteDy),
                            "– ALL KEYPADS — REFER TO PLAN", ControlTextAlign.Left);
                        Note(new XY(kx + ControlOnePlannerConstants.NoteDx, spineY - ControlOnePlannerConstants.NoteDy),
                            "(MAX 10 KEYPADS PER HOMERUN)", ControlTextAlign.Left);
                    }
                }
            }

            // ── HOME NETWORK node + CAT6 to each head's bottom-left ──
            if (headCat6Taps.Count > 0)
            {
                double trunkX = G.Layout.Cat6TrunkX;
                double topTapY = headCat6Taps[0].Y;
                var netCenter = new XY(trunkX, topTapY + G.Layout.HomeNetworkHeight + 2.0);
                symbols.Add(new ControlSymbolInstance(ControlSymbolKind.HomeNetwork, netCenter,
                    new Dictionary<string, string>()));
                double trunkTop = netCenter.Y - G.Layout.HomeNetworkHeight / 2.0;
                double trunkBot = headCat6Taps[headCat6Taps.Count - 1].Y;
                Wire(new XY(trunkX, trunkTop), new XY(trunkX, trunkBot), dashed: true);
                Mark(new XY(trunkX, (trunkTop + topTapY) / 2.0), ControlWireType.Cat6);
                foreach (var tap in headCat6Taps)
                    Wire(new XY(trunkX, tap.Y), tap, dashed: true);   // trunk → head bottom-left
            }

            var page = new ControlOneLineDrawing(1, 1, panelNodes, shadeNodes, symbols, wires, markers, notes,
                Array.Empty<ControlContinuation>());
            return new[] { page };
        }

        /// <summary>Per-link layout scratch for one enclosure's fan: the packed link, its pinned row, and the
        /// resolved downstream / lane / exit the drawing pass consumes. One per link across the enclosure's
        /// groups (2 for a PD8, 4 for an LV21).</summary>
        private sealed class LinkPlan
        {
            public PackedLink Link = default!;
            public int Row;
            public double Y;
            public bool IsCC;
            public bool Live;
            public List<PackedLinkUnit> Located = new List<PackedLinkUnit>();
            public bool HasKeypads;
            public double LaneX;
            public double ExitY;
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

        private static ControlPanelNode Node(ControlPanelRenderData rd, XY center)
            => new ControlPanelNode(center, rd.Name, rd.PartNumber, rd.FillText, rd.ModuleTiles,
                rd.LvSlots, rd.HostsProcessor, rd.EnclosureRole);

        private static void Tick(List<ControlWireSegment> wires, XY at, double half)
            => wires.Add(new ControlWireSegment(at.Offset(0, -half), at.Offset(0, half), dashed: false));
    }
}
