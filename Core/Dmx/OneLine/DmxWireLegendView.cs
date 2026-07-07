#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.OneLine
{
    /// <summary>
    /// One <b>per-job</b> wire-legend drawing, as a pure Revit-free set of primitives (BuildPlan Phase 6):
    /// a title note over a vertical list of rows, each a circled <see cref="DmxMarker"/> number paired with
    /// its wire-type label note. Unlike the one-line (one owned view per loop), there is exactly ONE legend
    /// view per job — its circled numbers are the same job-wide numbers the one-line stamps on every wire
    /// (both come from the same <see cref="DmxWireLegend"/>), so the legend and every loop stay 1:1.
    /// The shim renderer wipes the owned Drafting View and replays this, like the one-line.
    /// </summary>
    public sealed class DmxWireLegendDrawing
    {
        public DmxWireLegendDrawing(DmxNote title, IReadOnlyList<DmxMarker> markers, IReadOnlyList<DmxNote> notes)
        {
            Title = title;
            Markers = markers;
            Notes = notes;
        }

        /// <summary>The "WIRE LEGEND" title. Drawn separately (larger type) and centered over the row block by
        /// the shim, which measures the rendered rows — its X/alignment here are only a fallback.</summary>
        public DmxNote Title { get; }

        /// <summary>The circled legend numbers, one per legend row (the <see cref="Marker"/> family).</summary>
        public IReadOnlyList<DmxMarker> Markers { get; }

        /// <summary>One label note per row (the title is <see cref="Title"/>, not in here).</summary>
        public IReadOnlyList<DmxNote> Notes { get; }

        /// <summary>Deterministic owned-view name — a re-draw finds + wipes this one view (one per job).</summary>
        public string ViewName(string systemName) => $"TurboDMX — {systemName} — Wire Legend";
    }

    /// <summary>
    /// Lays a <see cref="DmxWireLegend"/> out into a <see cref="DmxWireLegendDrawing"/> (BuildPlan Phase 6) —
    /// the title, then one row per entry in canonical order (the entries are already ordered): a circled
    /// number on the left, the wire-type label to its right. Pure geometry off
    /// <see cref="DmxOneLineGeometry.Legend"/>; no Revit. Matches the firm's legend sample (number + label,
    /// no sample line — Specs/_DMX/Legend.txt).
    /// </summary>
    public static class DmxWireLegendPlanner
    {
        public static DmxWireLegendDrawing Build(DmxWireLegend legend)
        {
            var markers = new List<DmxMarker>();
            var notes = new List<DmxNote>();

            double markerX = DmxOneLineGeometry.Legend.MarkerX;
            double labelX = DmxOneLineGeometry.Legend.LabelX;

            // Title at the top — a larger (3/32") type. The shim centers it over the measured row block, so
            // the X here (over the number column) and Center alignment are only a fallback if measuring fails.
            var title = new DmxNote(new XY(markerX, 0.0), DmxOneLineGeometry.Legend.Title, DmxTextAlign.Center,
                                    DmxOneLineGeometry.Legend.TitleTextHeightFt);

            // A TextNote is top-anchored while the marker family is center-anchored, so raise each label's
            // insertion Y by half the cap height to put its glyph midline on the circled number's center.
            double labelNudge = DmxOneLineGeometry.Legend.LabelMidlineNudge;

            double y = -DmxOneLineGeometry.Legend.TitleGap;
            foreach (var entry in legend.Entries)
            {
                markers.Add(new DmxMarker(new XY(markerX, y), entry.Type, entry.Number));
                notes.Add(new DmxNote(new XY(labelX, y + labelNudge),
                                      entry.Label?.ToUpperInvariant() ?? string.Empty, DmxTextAlign.Left));
                y -= DmxOneLineGeometry.Legend.RowPitch;
            }

            return new DmxWireLegendDrawing(title, markers, notes);
        }
    }

    /// <summary>Outcome of drawing the per-job wire-legend view (BuildPlan Phase 6), surfaced back to the
    /// window. <see cref="ViewId"/> is the owned view (created or re-used) the ViewModel persists so the next
    /// draw finds and wipes the same one.</summary>
    public sealed class DmxWireLegendResult
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
