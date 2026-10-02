#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>One row of the generated control wire legend: its per-job number + the wire type it names.
    /// Mirrors <c>DmxWireLegendEntry</c> — a control wire type has no conductor-count variants, so the type
    /// alone identifies the row.</summary>
    public sealed class ControlWireLegendEntry
    {
        public ControlWireLegendEntry(int number, ControlWireType type)
        {
            Number = number;
            Type = type;
        }

        public int Number { get; }
        public ControlWireType Type { get; }
        public string Label => ControlWireLegend.LabelFor(Type);

        /// <summary>"1  QS CONTROL LINK" — the legend line.</summary>
        public override string ToString() => $"{Number}  {Label}";
    }

    /// <summary>
    /// The per-job control wire legend. Like the DMX legend, the firm numbers wire types <b>densely and
    /// per-job</b> — only the types actually drawn appear, numbered sequentially in a fixed canonical order so
    /// an absent conditional type is skipped (e.g. a shade-free job: QS=1, CAT6=2, Clear Connect=3, with no
    /// shade row in between). The same number is stamped on every wire of that type (the <c>WireMark</c>
    /// annotation) AND emitted into the legend, so number↔type is exactly 1:1 within a job. Built once from
    /// the solved pack and shared across every page.
    ///
    /// <para><b>Marker-tied roster — numbered CABLES only (decided 2026-10-01, refined for Clear Connect).</b>
    /// A numbered row exists only for a physical cable that is actually drawn and stamped:
    /// <list type="bullet">
    ///   <item>QS control link — always (the spine; panel-to-panel links are QS, drawn + labeled as QS).</item>
    ///   <item>CAT6 — always (the per-head Ethernet-to-Home-Network stub).</item>
    ///   <item>QS Sivoia shade control link — only when the job has shade panels.</item>
    /// </list>
    /// <b>Clear Connect Type A is NOT a numbered cable</b> — there is no CC wire. A wireless ("RF") link is QS
    /// wire from the processor to a Hybrid Repeater (the repeater is a QS device on the link; see
    /// <c>ProcessorLink.MaxRepeatersPerClearConnectLink</c>, "what makes a link a Clear Connect link at all"),
    /// and the wireless devices then reach the repeater over RF — no cable. So the RF portion is a <i>line
    /// style</i> convention (dashed RF / solid wired, Lutron-style), never a numbered row. <c>ClearConnect</c>,
    /// <c>PanelControlLink</c> and <c>DaliLoop</c> all stay <b>benched</b> — never rostered — because nothing
    /// draws them as a numbered cable (panel links are QS; DALI/DMX belong to TurboDALI/TurboDMX). If a future
    /// decision ever draws one, it lights up additively via <see cref="Build"/> with no rework.</para>
    /// </summary>
    public sealed class ControlWireLegend
    {
        /// <summary>Fixed canonical order the dense numbering walks. The two always-present types take the stable
        /// low numbers (QS=1, CAT6=2); the conditionals follow in this order when present.</summary>
        private static readonly ControlWireType[] CanonicalOrder =
        {
            ControlWireType.QsControlLink,
            ControlWireType.Cat6,
            ControlWireType.ShadeLink,
            // Clear Connect is intentionally absent — it is not a numbered cable (RF is a line style, not a wire).
        };

        private readonly Dictionary<ControlWireType, int> _numbers;

        private ControlWireLegend(IReadOnlyList<ControlWireLegendEntry> entries)
        {
            Entries = entries;
            _numbers = entries.ToDictionary(e => e.Type, e => e.Number);
        }

        /// <summary>The legend rows, in canonical order (QS, CAT6, then Shade / Clear Connect when present).</summary>
        public IReadOnlyList<ControlWireLegendEntry> Entries { get; }

        /// <summary>The job number for a wire type; 0 if the type isn't in this job's legend (e.g. a benched
        /// type, or a conditional type the job doesn't use). A marker drawn for an unrostered type would carry
        /// 0, which never happens because the planner only emits markers for rostered types.</summary>
        public int NumberFor(ControlWireType type) => _numbers.TryGetValue(type, out int n) ? n : 0;

        /// <summary>The firm's legend label for a wire type (exact wording where Lutron's own legend sets it,
        /// e.g. "QS SIVOIA SHADE CONTROL LINK").</summary>
        public static string LabelFor(ControlWireType type) => type switch
        {
            ControlWireType.QsControlLink => "QS CONTROL LINK",
            ControlWireType.Cat6 => "CAT6 NETWORK CABLE",
            // Shades are shown for control context only — the firm is not liable for shade wiring decisions.
            ControlWireType.ShadeLink => "SHADE WIRING BY OTHERS",
            ControlWireType.ClearConnect => "CLEAR CONNECT TYPE A",     // benched — RF is a line style, not a cable
            ControlWireType.PanelControlLink => "PANEL CONTROL LINK",   // benched — never rostered (panel links are QS)
            ControlWireType.DaliLoop => "DALI LOOP",                    // benched — never rostered (TurboDALI's job)
            ControlWireType.Input120V => "120V",                        // renderer glyph — never a marked row
            _ => "",
        };

        /// <summary>Build the legend from the wire types a job actually draws, numbered densely 1..N in the
        /// fixed canonical order (absent types skipped). Duplicate types collapse to one row.</summary>
        public static ControlWireLegend Build(IEnumerable<ControlWireType> usedTypes)
        {
            var used = new HashSet<ControlWireType>(usedTypes ?? Enumerable.Empty<ControlWireType>());
            var entries = new List<ControlWireLegendEntry>();
            int next = 1;
            foreach (var t in CanonicalOrder)
                if (used.Contains(t))
                    entries.Add(new ControlWireLegendEntry(next++, t));
            return new ControlWireLegend(entries);
        }

        /// <summary>Build the job legend straight off the solved pack: QS + CAT6 always, plus the Sivoia shade
        /// link when any link carries a shade unit. A wireless (Clear Connect) link adds NO numbered row — its
        /// cable to the repeater is QS, and the RF beyond it is a line style, not a cable. This reads the same
        /// signals the planner draws from, so the numbered legend and the stamped markers stay 1:1.</summary>
        public static ControlWireLegend ForPack(LinkPackResult? pack)
        {
            // QS and CAT6 are definitional for a Lutron control job (every processor has a QS spine and an
            // Ethernet-to-Home-Network stub), so they are always present.
            var used = new List<ControlWireType> { ControlWireType.QsControlLink, ControlWireType.Cat6 };

            bool anyShade = false;
            foreach (var group in pack?.Processors ?? Enumerable.Empty<ProcessorGroup>())
                foreach (var link in new[] { group.Link1, group.Link2 })
                    if (link?.Units != null && link.Units.Any(u => u.Category == LinkCategory.Shades))
                        anyShade = true;

            if (anyShade) used.Add(ControlWireType.ShadeLink);
            return Build(used);
        }
    }

    /// <summary>
    /// One <b>per-job</b> control wire-legend drawing, as a pure Revit-free set of primitives: a title note
    /// over a vertical list of rows, each a circled <see cref="ControlMarker"/> number paired with its
    /// wire-type label note. Mirrors <c>DmxWireLegendDrawing</c>. Unlike the one-line (one owned view per
    /// page), there is exactly ONE legend view per job — its circled numbers are the same job-wide numbers the
    /// one-line stamps on every wire (both come from the same <see cref="ControlWireLegend"/>), so the legend
    /// and every page stay 1:1. The shim renderer wipes the owned Drafting View and replays this.
    /// </summary>
    public sealed class ControlWireLegendDrawing
    {
        public ControlWireLegendDrawing(ControlNote title, IReadOnlyList<ControlMarker> markers,
            IReadOnlyList<ControlNote> notes, IReadOnlyList<ControlWireSegment> sampleLines)
        {
            Title = title;
            Markers = markers;
            Notes = notes;
            SampleLines = sampleLines;
        }

        /// <summary>The "WIRE LEGEND" title. Drawn separately (larger type) and centered over the row block by
        /// the shim, which measures the rendered rows — its X/alignment here are only a fallback.</summary>
        public ControlNote Title { get; }

        /// <summary>The circled legend numbers, one per NUMBERED legend row (the wire-mark annotation family).</summary>
        public IReadOnlyList<ControlMarker> Markers { get; }

        /// <summary>One label note per row — the numbered-cable labels AND the two line-style key labels ("RF
        /// CONNECTION" / "WIRED CONNECTION"). The title is <see cref="Title"/>, not in here.</summary>
        public IReadOnlyList<ControlNote> Notes { get; }

        /// <summary>The unnumbered line-style KEY segments at the bottom of the legend (Lutron 609): a dashed
        /// sample for "RF CONNECTION" and a solid sample for "WIRED CONNECTION". Drawn by the shim with the same
        /// dashed/solid styles the one-line uses, so the key matches the sheet.</summary>
        public IReadOnlyList<ControlWireSegment> SampleLines { get; }

        /// <summary>Deterministic owned-view name — a re-draw finds + wipes this one view (one per job).</summary>
        public string ViewName(string systemName) => $"{systemName} - Wire Legend";
    }

    /// <summary>
    /// Lays a <see cref="ControlWireLegend"/> out into a <see cref="ControlWireLegendDrawing"/> — the title,
    /// then one row per entry in canonical order: a circled number on the left, the wire-type label to its
    /// right. Pure geometry off <see cref="ControlOneLineGeometry.Legend"/>; no Revit. Mirrors
    /// <c>DmxWireLegendPlanner</c>.
    /// </summary>
    public static class ControlWireLegendPlanner
    {
        public static ControlWireLegendDrawing Build(ControlWireLegend legend)
        {
            var markers = new List<ControlMarker>();
            var notes = new List<ControlNote>();

            double markerX = ControlOneLineGeometry.Legend.MarkerX;
            double labelX = ControlOneLineGeometry.Legend.LabelX;

            // Title at the top — its own larger firm style (TitleTextTypeName), standing out above the 4.5" body
            // rows (DMX legend look). The shim centers it over the measured row block, so the X here (over the
            // number column) and Center alignment are only a fallback if measuring fails.
            var title = new ControlNote(new XY(markerX, ControlOneLineGeometry.Legend.TitleHeadroom),
                ControlOneLineGeometry.Legend.Title, ControlTextAlign.Center,
                ControlOneLineGeometry.Legend.TitleTextHeightFt, ControlOneLineGeometry.Legend.TitleTextTypeName);

            // A TextNote is top-anchored while the marker family is center-anchored, so raise each label's
            // insertion Y by half the cap height to put its glyph midline on the circled number's center.
            double labelNudge = ControlOneLineGeometry.Legend.LabelMidlineNudge;

            // The symbol column (circled numbers + sample-line keys) is dropped by SymbolDy relative to the text
            // so each glyph lines up with its label; the labels stay at y + labelNudge.
            double symDy = ControlOneLineGeometry.Legend.SymbolDy;

            double y = -ControlOneLineGeometry.Legend.TitleGap;
            foreach (var entry in legend.Entries)
            {
                markers.Add(new ControlMarker(new XY(markerX, y + symDy), entry.Type, entry.Number));
                // Row labels at the firm 4.5" style (the sheet-wide baseline) — the title above is larger.
                notes.Add(new ControlNote(new XY(labelX, y + labelNudge), entry.Label, ControlTextAlign.Left,
                    textTypeName: ControlOneLineGeometry.LargeTextTypeName));
                y -= ControlOneLineGeometry.Legend.RowPitch;
            }

            // The two unnumbered line-style keys (Lutron 609): a dashed RF sample, then a solid WIRED sample.
            // Below the numbered rows with an extra gap. The sample line is centered on the marker column.
            var sampleLines = new List<ControlWireSegment>();
            double half = ControlOneLineGeometry.Legend.SampleLineHalfLen;
            void StyleKey(bool dashed, string label)
            {
                sampleLines.Add(new ControlWireSegment(new XY(markerX - half, y + symDy), new XY(markerX + half, y + symDy), dashed));
                notes.Add(new ControlNote(new XY(labelX, y + labelNudge), label, ControlTextAlign.Left,
                    textTypeName: ControlOneLineGeometry.LargeTextTypeName));   // 4.5" row body
                y -= ControlOneLineGeometry.Legend.RowPitch;
            }

            y -= ControlOneLineGeometry.Legend.StyleKeyGap - ControlOneLineGeometry.Legend.RowPitch;
            StyleKey(dashed: true, "RF CONNECTION");
            StyleKey(dashed: false, "WIRED CONNECTION");

            return new ControlWireLegendDrawing(title, markers, notes, sampleLines);
        }
    }

    /// <summary>Outcome of drawing the per-job wire-legend view, surfaced back to the window.
    /// <see cref="ViewId"/> is the owned view (created or re-used) the ViewModel persists so the next draw finds
    /// and wipes the same one. Mirrors <c>DmxWireLegendResult</c>.</summary>
    public sealed class ControlWireLegendResult
    {
        /// <summary>The owned Drafting View's element id (the Revit-free long). 0 ⇒ the draw failed.</summary>
        public long ViewId { get; set; }

        /// <summary>True when this run created the view; false when it re-used + wiped an existing one.</summary>
        public bool Created { get; set; }

        public int Rows { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        public bool Ok => ViewId != 0L;

        public string Summary =>
            (Ok ? (Created ? "Drew " : "Redrew ") : "Failed to draw ") + "the wire legend"
            + (Ok ? $": {Rows} row(s)" : "")
            + (Warnings.Count > 0 ? $" ({Warnings.Count} warning(s))" : "") + ".";
    }
}
