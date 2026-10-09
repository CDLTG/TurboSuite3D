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
    /// lane + lowest exit) — dropping to each row's spine (matching the Lutron reference sheets). Downstream panels/shades hang
    /// along each QS row in rule-#5 order (Modules → Shades → Keypads, natural-name within a category); keypads
    /// collapse to one tail stub; a Clear Connect link doglegs to its row and draws a wireless stub. Each head
    /// draws its own Ethernet-to-Home-Network stub out its left edge (Lutron-style — no shared switch node).</para>
    ///
    /// <para><b>Pagination (stage 2, done):</b> enclosures pack onto 42×30 pages IN PACKER ORDER, each
    /// enclosure indivisible. An enclosure is ≤4 link-rows (an LV21 = 2 processors = 4 links), and a page holds
    /// 4 rows, so an enclosure never splits — it always fits a fresh page. EVERY tie is enclosure-local (QS
    /// links, the head fan, 120V, shades, keypads, the Ethernet stub), so nothing crosses a page boundary and
    /// the drawing needs no continuation bubbles. The return type was always a page list, so pagination stayed
    /// additive.</para>
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
        /// <param name="repeaterPartNumber">The hybrid-repeater catalog number stamped on each CC-A chain
        /// node (<c>ControlRepeaterDetail</c>'s only label). Null/empty ⇒ the stamp draws with a blank part
        /// number. One job-wide value today (the dominant repeater model); per-link catalogs can come later.</param>
        public static IReadOnlyList<ControlOneLineDrawing> Build(
            LinkPackResult pack,
            IReadOnlyDictionary<string, ControlPanelRenderData> panels,
            string systemName,
            string? repeaterPartNumber = null)
        {
            var groups = pack?.Processors ?? Array.Empty<ProcessorGroup>();
            if (groups.Count == 0) return Array.Empty<ControlOneLineDrawing>();

            // The per-job wire legend owns the dense marker numbering — built off the SAME pack the markers are
            // stamped from (QS + CAT6 always; Shade / Clear Connect by presence), so the stamped numbers and the
            // legend view stay 1:1. The VM rebuilds this cheaply (pure) to draw the legend view.
            var legend = ControlWireLegend.ForPack(pack);

            // Per-PAGE accumulators — the drawing helpers below close over these variables, and the page loop
            // (phase 2) re-points them at a fresh set per page, so every helper targets the page being built.
            var panelNodes = new List<ControlPanelNode>();
            var shadeNodes = new List<ControlShadeNode>();
            var repeaterNodes = new List<ControlRepeaterNode>();
            var wires = new List<ControlWireSegment>();
            var glyphNodes = new List<ControlGlyphNode>();
            var markers = new List<ControlMarker>();
            var notes = new List<ControlNote>();

            var natural = new NaturalStringComparer();

            // Line-style convention (Lutron 609): dashed = RF (wireless), solid = WIRED. Every cable here —
            // QS, CAT6, shade link, 120 V — is a WIRED run, so it draws SOLID (dashed: false); the only dashed
            // segment is the repeater→keypad RF tail, which is deferred to the keypad-location expansion (so
            // nothing is dashed today). The wire TYPE is carried by the circled marker number, not the style.
            void Wire(XY a, XY b, bool dashed) => wires.Add(new ControlWireSegment(a, b, dashed));
            // Place a list-row glyph family (keypad / shade / wireless) at a point — the shim resolves it by
            // role and places the authored 4.5" symbol there.
            void Glyph(XY at, string role) => glyphNodes.Add(new ControlGlyphNode(at, role));
            void Mark(XY at, ControlWireType t) => markers.Add(new ControlMarker(at, t, legend.NumberFor(t)));
            void Note(XY at, string text, ControlTextAlign align, string? typeName = null,
                ControlVerticalAlign vAlign = ControlVerticalAlign.Top)
                => notes.Add(new ControlNote(at, text, align, textTypeName: typeName, vAlign: vAlign));

            // A plain hollow square as 4 solid segments (renderer glyph — NOT a marker / legend key).
            void Square(XY c, double s)
            {
                double x0 = c.X - s / 2.0, x1 = c.X + s / 2.0, y0 = c.Y - s / 2.0, y1 = c.Y + s / 2.0;
                Wire(new XY(x0, y0), new XY(x1, y0), dashed: false);
                Wire(new XY(x1, y0), new XY(x1, y1), dashed: false);
                Wire(new XY(x1, y1), new XY(x0, y1), dashed: false);
                Wire(new XY(x0, y1), new XY(x0, y0), dashed: false);
            }

            // One repeater's keypad fan (F4): rises from the TOP of the stamp, mirroring the shade motor
            // list — a solid tap stub to a terminus (its "marker" slot carries the wireless glyph, not a
            // circled number), then a single bottom-up column of [Switch ID] Room - Model rows, each with
            // the wireless glyph. No RF line style / no legend entry — "wireless" is the glyph. Never wraps
            // (a repeater never carries 20+ keypads).
            void DrawRepeaterFan(double cx, double baseY, IReadOnlyList<KeypadRecord> keypads)
            {
                double stampTopY = baseY + G.Repeater.Height;      // origin at baseY, art rises to +Height
                var t0 = new XY(cx, stampTopY);
                var t1 = t0.Offset(0, G.Layout.MotorTapStubLength);
                // The tap BREAKS around the wireless glyph (unlike the shade motor tap): a leg up to the gap
                // low, then a clear gap the glyph sits in, then the leg resumes at the gap high to the terminus.
                Wire(t0, t0.Offset(0, G.Layout.RepeaterTapGapLow), dashed: false);     // lower leg
                Wire(t0.Offset(0, G.Layout.RepeaterTapGapHigh), t1, dashed: false);    // upper leg (past the gap)
                Glyph(new XY(cx, t0.Y + (G.Layout.RepeaterTapGapLow + G.Layout.RepeaterTapGapHigh) / 2.0),
                    Roles.ControlWirelessGlyph);                    // centered in the gap
                Wire(t1.Offset(-G.Layout.MotorTerminusHalf, 0), t1.Offset(G.Layout.MotorTerminusHalf, 0),
                    dashed: false);                                 // terminus

                if (keypads.Count == 0) return;
                var labels = keypads
                    .OrderBy(k => string.IsNullOrEmpty(k.SwitchId))
                    .ThenBy(k => k.SwitchId, natural)
                    .Select(ControlListLayout.KeypadRowLabel)
                    .ToList();
                // Center the [glyph + text] block on the stamp. The shade list aligns its text to the 120V
                // label instead, but the repeater is narrower than that offset, so a measured center keeps
                // the fan over the stamp. Block runs glyph → text; shift the glyph left by half its width.
                int maxChars = labels.Max(l => l.Length);
                double contentWidth = G.Layout.KeypadListTextDx + maxChars * G.Layout.KeypadListCharWidth;
                var anchor = new XY(cx - contentWidth / 2.0, t1.Y + G.Layout.MotorListAnchorDy);
                var positions = ControlListLayout.Layout(labels.Count, anchor,
                    rowsPerColumn: labels.Count + 1, G.Layout.KeypadListRowPitch, columnWidth: 0.0);
                for (int i = 0; i < positions.Count; i++)
                {
                    XY p = positions[i].Anchor;
                    Glyph(p, Roles.ControlWirelessGlyph);
                    Note(p.Offset(G.Layout.KeypadListTextDx, 0), labels[i], ControlTextAlign.Left,
                        G.LargeTextTypeName, ControlVerticalAlign.Middle);
                }
            }

            // 120 V feed, following the Lutron reference sheet: up from the panel top edge, LEFT to a terminus square,
            // with "120V" (firm style) above it. All renderer-drawn; the square is a plain glyph, not a legend key.
            void Add120V(XY center, double height)
            {
                double topY = G.Panel.TopEdgeY(center.Y, height);
                double legX = center.X + G.Layout.Feed120VLegDx;
                double cornerY = topY + G.Layout.Feed120VRise;
                double termX = legX - G.Layout.Feed120VRun;   // horizontal run terminus = the square's right edge
                Wire(new XY(legX, topY), new XY(legX, cornerY), dashed: false);        // up from the panel top (wired → solid)
                Wire(new XY(legX, cornerY), new XY(termX, cornerY), dashed: false);    // left to the terminus
                Square(new XY(termX - G.Layout.Feed120VSquare / 2.0, cornerY), G.Layout.Feed120VSquare);
                Note(new XY(termX + G.Layout.Feed120VLabelDx, cornerY + G.Layout.Feed120VLabelAboveCorner),
                    "120V", ControlTextAlign.Left, G.LargeTextTypeName);
            }

            double colX = G.Layout.ProcessorColumnX;
            double headRightX = colX + G.Panel.Width / 2.0;
            double headLeftX = colX - G.Panel.Width / 2.0;
            double col1LeftX = headRightX + G.Layout.HeadColumnGap;   // column-1 panels start here; the gap holds the fan

            // Rows are LINKS at a uniform origin-to-origin pitch; row 0 sits at Y=0 and each row below is one
            // RowPitch lower. The row index is PER PAGE (0..3), so every sheet's content occupies the same band.
            double RowCenterY(int r) => -r * G.Layout.RowPitch;

            // Ethernet-to-Home-Network stub: a CAT6 run LEFT out of the head's left edge, with a two-line label
            // and a CAT6 wire marker (the legend key, #5), per the Lutron reference sheet. Per head — there is NO
            // shared switch node or trunk, so nothing ties heads together or crosses a page boundary.
            void AddEthernet(double headBottomY)
            {
                double y = headBottomY + G.Layout.EthernetStubAboveOrigin;      // inline with the bottom-row exit
                var a = new XY(headLeftX, y);                                   // start at the head's left edge
                var b = new XY(headLeftX - G.Layout.EthernetStubLen, y);        // run 6' LEFT
                Wire(a, b, dashed: false);   // CAT6 is a wired run → solid
                Mark(new XY(headLeftX - G.Layout.MarkerExitOffset, y), ControlWireType.Cat6);   // fixed offset off the head LEFT edge
                Note(new XY(colX + G.Layout.EthernetLabelDx, headBottomY + G.Layout.EthernetLabelDy),
                    "ETHERNET LINK TO\rHOME NETWORK", ControlTextAlign.Center, G.LargeTextTypeName);
            }

            // ── Phase 0: resolve every physical enclosure (NO drawing yet). Walk the groups MERGING consecutive
            //    groups that share a processor panel name into one enclosure: an LV21 = two processor compartments
            //    = two same-named groups = ONE head with up to 4 link rows (per the Lutron reference sheet). Groups with no name
            //    (tests / legacy) each stand alone, the head then found by scanning link units (the PD8 that is
            //    also a Modules unit). Resolving here — before any drawing — lets the pager below know each
            //    enclosure's row count so it can assign pages. ──
            var enclosures = new List<Enclosure>();
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

                // The enclosure's links flattened in packer order (link1 then link2, per group). Liveness is
                // resolved below; a completely empty link (0 devices/loads, e.g. the spare QS link on a processor
                // added only to host a Clear Connect link) is dropped — it consumes no row.
                var plans = new List<LinkPlan>();
                foreach (var g in encGroups)
                {
                    plans.Add(new LinkPlan { Link = g.Link1 });
                    plans.Add(new LinkPlan { Link = g.Link2 });
                }

                // ── Identify the head: by enclosure name first (the ONLY way to see a module-less LV21), else by
                //    scanning the links' units for a processor-hosting panel (a PD8 that is also a Modules unit —
                //    the case the tests build without a name), else a P# stub. ──
                ControlPanelRenderData? headRd = null;
                // `is { Length: > 0 }` (not IsNullOrEmpty) so net48 flow-analysis sees encName is non-null here —
                // net48's BCL lacks the [NotNullWhen(false)] annotation that keeps net8 quiet, hence a CS8604
                // false positive on the guarded TryGetValue otherwise.
                if (encName is { Length: > 0 } && panels.TryGetValue(encName, out var byName) && byName.HostsProcessor)
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

                // ── Resolve each link's downstream (rule-#5 ordered, head excluded) + which links are LIVE ──
                foreach (var pl in plans)
                {
                    if (pl.Link.IsClearConnect) { pl.IsCC = true; pl.Live = true; continue; }
                    var ordered = Order(pl.Link.Units, excludeName: encName, natural);
                    pl.Located = ordered.Where(u => u.Category != LinkCategory.Keypads).ToList();
                    pl.HasKeypads = ordered.Any(u => u.Category == LinkCategory.Keypads);
                    pl.Live = pl.Located.Count > 0 || pl.HasKeypads;   // empty QS link → dropped, no row
                }

                var live = plans.Where(pl => pl.Live).ToList();
                enclosures.Add(new Enclosure
                {
                    Name = encName,
                    HeadRd = headRd,
                    HeadH = PanelHeight(headRd),
                    Live = live,
                    RowCount = System.Math.Max(1, live.Count),   // a head with no live links still reserves one row
                });
            }

            if (enclosures.Count == 0) return Array.Empty<ControlOneLineDrawing>();

            // ── Phase 1: paginate. Pack enclosures onto 42×30 pages IN PACKER ORDER, each enclosure indivisible.
            //    A page holds up to MaxRowsPerPage link-rows; the next enclosure starts a new page when it would
            //    overflow the rows left on the current one. In the Lutron domain an enclosure never exceeds 4
            //    rows (an LV21 = 2 processors = 4 links), so it always fits a fresh page — a hypothetical >4-row
            //    enclosure still lands alone on its own page (guarded by the domain, not the code: there is no
            //    3-processor enclosure). ──
            const int MaxRowsPerPage = 4;
            int curPage = 1, rowsOnPage = 0;
            foreach (var enc in enclosures)
            {
                if (rowsOnPage > 0 && rowsOnPage + enc.RowCount > MaxRowsPerPage)
                {
                    curPage++;
                    rowsOnPage = 0;
                }
                enc.Page = curPage;
                enc.PageRowStart = rowsOnPage;
                rowsOnPage += enc.RowCount;
            }
            int pageCount = curPage;

            // Draw one enclosure's head + link fan at its per-page row band, appending to the CURRENT page's
            // buckets. The row index is per page (enc.PageRowStart .. +RowCount-1), so each sheet uses the same
            // 42×30 band. The local helpers (Wire/Note/Mark/Add120V/…) target whatever the page loop last
            // pointed the accumulators at.
            void DrawEnclosure(Enclosure enc)
            {
                var live = enc.Live;
                var headRd = enc.HeadRd;
                double headH = enc.HeadH;
                string? encName = enc.Name;
                int rowStart = enc.PageRowStart;

                // Pin each live link to its per-page row — dead links already dropped, so live links stack
                // consecutively with no 28' blank band where a dead link would have sat.
                for (int idx = 0; idx < live.Count; idx++)
                {
                    live[idx].Row = rowStart + idx;
                    live[idx].Y = RowCenterY(rowStart + idx);
                }

                // Head is BOTTOM-ALIGNED to its first (top) row's baseline, in the left column (per the Lutron reference sheet).
                // headBottomY is the head's ORIGIN Y (center-bottom of the artwork) — the datum link exits anchor to.
                double headBottomY = RowCenterY(rowStart) - G.Panel.PowerPanelHeight / 2.0;
                var headCenter = new XY(colX, headBottomY + headH / 2.0);

                panelNodes.Add(Node(headRd, headCenter));
                Add120V(headCenter, headH);    // 120 V feed on top
                AddEthernet(headBottomY);      // CAT6 stub LEFT to the home network (per head, Lutron-style)

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

                    // Dogleg out of the head: right edge → lane (the bend X, in the gap), then DROP the lane to
                    // the link's spine. The spine + carets below are IDENTICAL for a QS link and a CC-A (RF)
                    // link: a node hangs off a caret whose apex sits on its bottom origin, so swapping a
                    // repeater for a dimmer/shade along the link changes only what is drawn ABOVE the caret,
                    // never the link itself.
                    Wire(new XY(headRightX, ey), new XY(lx, ey), dashed: false);   // exit horizontal

                    // The spine runs LinkSpineDropFt BELOW the shared bottom-origin baseline; every child node
                    // (dimmer, shade, OR repeater — all bottom-aligned) taps it with a caret whose apex sits ON
                    // its origin (per the Lutron reference sheets). A terminal node with nothing after it draws
                    // only the LEFT half of its caret, so no half-caret dangles past it.
                    double panelBottomY = y - G.Panel.PowerPanelHeight / 2.0;
                    double spineY = panelBottomY - G.Layout.LinkSpineDropFt;
                    Wire(new XY(lx, ey), new XY(lx, spineY), dashed: false);       // drop the lane to the spine
                    Mark(new XY(headRightX + G.Layout.MarkerExitOffset, ey), ControlWireType.QsControlLink);   // at the head RIGHT-edge exit

                    double cursorX = col1LeftX - G.Layout.NodeGap;   // so the first QS node lands at col1LeftX + w/2
                    double spineFromX = lx;                          // spine starts at the lane drop

                    void Caret(double cxAt, bool leftHalfOnly)
                    {
                        double leftFoot = cxAt - G.Layout.CaretHalfWidth;
                        Wire(new XY(spineFromX, spineY), new XY(leftFoot, spineY), dashed: false);
                        Wire(new XY(leftFoot, spineY), new XY(cxAt, panelBottomY), dashed: false);
                        if (leftHalfOnly) { spineFromX = cxAt; return; }
                        double rightFoot = cxAt + G.Layout.CaretHalfWidth;
                        Wire(new XY(cxAt, panelBottomY), new XY(rightFoot, spineY), dashed: false);
                        spineFromX = rightFoot;
                    }

                    if (pl.IsCC)
                    {
                        // A CC-A (RF) link is QS WIRE from the processor to each Hybrid Repeater — the wireless
                        // devices reach the repeater over RF, there is no Clear Connect cable — so it draws on the
                        // SAME spine + carets as a QS link. Each repeater (≤4) hangs off a caret as a
                        // bottom-aligned stamp, spaced by the link's own CENTER-TO-CENTER values (so a repeater
                        // sits where a panel would): the first at the head→column-1 c-c off the head center, then
                        // the inter-panel c-c between repeaters.
                        //
                        // When the demand carries per-repeater RECORDS (located path), each stamp fans ITS OWN
                        // keypads rising from the stamp top, and no tail stub is drawn (the last caret is
                        // terminal). The location-free global pour knows only a repeater COUNT, so it draws plain
                        // stamps and the aggregate WIRELESS KEYPADS stub (the keypad→repeater identity is unknown
                        // there).
                        var repRecords = pl.Link.RepeaterRecords;
                        bool perRepeater = repRecords.Count > 0;
                        int reps = perRepeater ? repRecords.Count : System.Math.Max(0, pl.Link.Repeaters);
                        double lastCx = double.NaN;
                        for (int r = 0; r < reps; r++)
                        {
                            double cx = colX + G.Layout.HeadColumnCenterToCenter + r * G.Layout.PanelCenterToCenter;
                            Caret(cx, leftHalfOnly: perRepeater && r == reps - 1);   // records → no tail after last
                            var stampCenter = new XY(cx, panelBottomY + G.Repeater.Height / 2.0);
                            repeaterNodes.Add(new ControlRepeaterNode(stampCenter, repeaterPartNumber ?? string.Empty));
                            Add120V(stampCenter, G.Repeater.Height);   // 120 V feed, same as every other node
                            if (perRepeater) DrawRepeaterFan(cx, panelBottomY, repRecords[r].Keypads);
                            lastCx = cx;
                        }

                        if (!perRepeater)
                        {
                            // Aggregate WIRELESS KEYPADS stub — spine terminus + tick + note, the same edge+gap
                            // shape as the QS keypad tail. With no repeater modelled (a 0/4 link) it lands at the
                            // first-node column so it still sits on the link.
                            double tailFromX = double.IsNaN(lastCx)
                                ? colX + G.Layout.HeadColumnCenterToCenter
                                : lastCx + G.Repeater.Width / 2.0;
                            double kxCc = tailFromX + G.Layout.KeypadTailGap;
                            Wire(new XY(spineFromX, spineY), new XY(kxCc, spineY), dashed: false);
                            Tick(wires, new XY(kxCc, spineY), G.Layout.KeypadTickHalf);
                            Note(new XY(kxCc + G.Layout.KeypadTailBlockDx, spineY),
                                "WIRELESS KEYPADS", ControlTextAlign.Left, G.LargeTextTypeName,
                                ControlVerticalAlign.Middle);
                        }
                        continue;
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

                            var motorRecs = u.Motors;
                            if (motorRecs.Count > 0)
                            {
                                // Per-motor list (Phase E): a lengthened tap stub to a horizontal terminus,
                                // then the list block offset LEFT so its row text left-aligns with the shade's
                                // 120V label — clearing the keypad columns to the right. ≤10 motors per
                                // QSPS-10PNL ⇒ always one column rising from the terminus.
                                var m1 = m0.Offset(0, G.Layout.MotorTapStubLength);
                                Wire(m0, m1, dashed: false);
                                Mark(new XY(m0.X, (m0.Y + m1.Y) / 2.0), ControlWireType.ShadeLink);
                                Wire(m1.Offset(-G.Layout.MotorTerminusHalf, 0),
                                     m1.Offset(G.Layout.MotorTerminusHalf, 0), dashed: false);   // 9" terminus

                                var motorLabels = motorRecs.Select(ControlListLayout.MotorRowLabel).ToList();
                                // Align row text left with the 120V label; the glyph sits one TextDx to its left.
                                double term120X = c.X + G.Layout.Feed120VLegDx - G.Layout.Feed120VRun;
                                double anchorX = term120X + G.Layout.Feed120VLabelDx - G.Layout.KeypadListTextDx;
                                var mAnchor = new XY(anchorX, m1.Y + G.Layout.MotorListAnchorDy);
                                var mPos = ControlListLayout.Layout(motorLabels.Count, mAnchor,
                                    ShadeSolver.ShadesPerPanel, G.Layout.KeypadListRowPitch, columnWidth: 0.0);
                                for (int mi = 0; mi < mPos.Count; mi++)
                                {
                                    XY p = mPos[mi].Anchor;
                                    Glyph(p, Roles.ControlShadeGlyph);   // motor glyph (shade symbol)
                                    Note(p.Offset(G.Layout.KeypadListTextDx, 0), motorLabels[mi],
                                        ControlTextAlign.Left, G.LargeTextTypeName, ControlVerticalAlign.Middle);
                                }
                            }
                            else
                            {
                                // Legacy stub: no per-motor records (count-only path) — short tap + n MOTORS.
                                var m1 = m0.Offset(0, G.Layout.FeedStubLength);
                                Wire(m0, m1, dashed: false);
                                Mark(new XY(m0.X, (m0.Y + m1.Y) / 2.0), ControlWireType.ShadeLink);
                                Note(m1.Offset(ControlOnePlannerConstants.NoteDx, ControlOnePlannerConstants.NoteDy),
                                    $"{motors} MOTORS", ControlTextAlign.Left, G.LargeTextTypeName);
                            }
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
                        Wire(new XY(spineFromX, spineY), new XY(kx, spineY), dashed: false);
                        Tick(wires, new XY(kx, spineY), G.Layout.KeypadTickHalf);

                        var keypadRecs = pl.Link.KeypadRecords;
                        if (keypadRecs.Count > 0)
                        {
                            // Located list (Phase D): one row per keypad, bottom-up columns wrapping right,
                            // replacing the "ALL KEYPADS" stub. Switch-ID order, unnumbered rows last.
                            var sorted = keypadRecs
                                .OrderBy(r => string.IsNullOrEmpty(r.SwitchId))
                                .ThenBy(r => r.SwitchId, natural)
                                .ToList();
                            var labels = sorted.Select(ControlListLayout.KeypadRowLabel).ToList();

                            // Column width is dynamic — the longest label in the list — so a column never
                            // overlaps the next regardless of room-name length.
                            int maxChars = labels.Count > 0 ? labels.Max(l => l.Length) : 0;
                            double columnWidth = G.Layout.KeypadListTextDx
                                + maxChars * G.Layout.KeypadListCharWidth
                                + G.Layout.KeypadListColumnPadding;

                            var anchor = new XY(kx + G.Layout.KeypadTailBlockDx,
                                spineY + G.Layout.KeypadListAnchorDy);
                            var positions = ControlListLayout.Layout(labels.Count, anchor,
                                G.Layout.KeypadListRowsPerColumn, G.Layout.KeypadListRowPitch,
                                columnWidth,
                                G.Layout.KeypadListHomerunSize, G.Layout.KeypadListHomerunGap);
                            for (int i = 0; i < positions.Count; i++)
                            {
                                XY p = positions[i].Anchor;
                                Glyph(p, Roles.ControlKeypadGlyph);   // keypad glyph (▽, plan symbol)
                                // Middle vertical-align so the text centers on the glyph's point (the row
                                // notes default to Top otherwise, hanging the text below the glyph).
                                Note(p.Offset(G.Layout.KeypadListTextDx, 0), labels[i],
                                    ControlTextAlign.Left, G.LargeTextTypeName, ControlVerticalAlign.Middle);
                            }
                            // The homerun wiring rule, centered on the spine terminus wire.
                            Note(new XY(kx + G.Layout.KeypadTailBlockDx, spineY),
                                "KEYPADS (MAX 10 PER HOMERUN)", ControlTextAlign.Left, G.LargeTextTypeName,
                                ControlVerticalAlign.Middle);
                        }
                        else
                        {
                            // Legacy stub: location-less keypads (room unmapped), or no records. Two-line
                            // note, centered on the spine terminus wire (same trick as the located note).
                            Note(new XY(kx + G.Layout.KeypadTailBlockDx, spineY),
                                "ALL KEYPADS\r(MAX 10 KEYPADS PER HOMERUN)",
                                ControlTextAlign.Left, G.LargeTextTypeName, ControlVerticalAlign.Middle);
                        }
                    }
                }
            }

            // ── Phase 2: draw each page independently — fresh buckets, per-page row band. Each sheet is fully
            //    self-contained: every tie is enclosure-local (QS links, the head fan, 120V, shades, keypads, and
            //    each head's own Ethernet-to-Home-Network stub), and an enclosure never splits across pages, so
            //    nothing crosses a page boundary and no continuation bubbles are needed. ──
            var drawings = new List<ControlOneLineDrawing>();
            for (int pg = 1; pg <= pageCount; pg++)
            {
                // Re-point the accumulators at THIS page's buckets (the drawing helpers close over them).
                panelNodes = new List<ControlPanelNode>();
                shadeNodes = new List<ControlShadeNode>();
                repeaterNodes = new List<ControlRepeaterNode>();
                wires = new List<ControlWireSegment>();
                glyphNodes = new List<ControlGlyphNode>();
                markers = new List<ControlMarker>();
                notes = new List<ControlNote>();

                foreach (var enc in enclosures)
                    if (enc.Page == pg) DrawEnclosure(enc);

                drawings.Add(new ControlOneLineDrawing(pg, pageCount, panelNodes, shadeNodes, wires,
                    markers, notes, repeaterNodes, glyphNodes));
            }
            return drawings;
        }

        /// <summary>One physical processor enclosure resolved in phase 0: its head render data, its LIVE links
        /// (rule-#5 ordered downstream already computed), and the row span it occupies. The pager (phase 1) sets
        /// <see cref="Page"/> + <see cref="PageRowStart"/>; the drawer (phase 2) reads them. A PD8 = 1 group = up
        /// to 2 rows; an LV21 = 2 same-named groups merged = up to 4 rows.</summary>
        private sealed class Enclosure
        {
            public string? Name;
            public ControlPanelRenderData HeadRd = default!;
            public double HeadH;
            public List<LinkPlan> Live = new List<LinkPlan>();
            public int RowCount;         // rows this enclosure occupies = max(1, Live.Count)
            public int Page;             // 1-based page assignment (phase 1)
            public int PageRowStart;     // first row index WITHIN its page (0..3) (phase 1)
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

        private static double PanelHeight(ControlPanelRenderData rd)
            => G.Panel.Height(rd.ModuleTiles.Count, rd.LvSlots.Count);

        private static ControlPanelNode Node(ControlPanelRenderData rd, XY center)
            => new ControlPanelNode(center, rd.Name, rd.PartNumber, rd.FillText, rd.ModuleTiles,
                rd.LvSlots, rd.HostsProcessor, rd.EnclosureRole);

        private static void Tick(List<ControlWireSegment> wires, XY at, double half)
            => wires.Add(new ControlWireSegment(at.Offset(0, -half), at.Offset(0, half), dashed: false));
    }
}
