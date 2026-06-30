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
        public DmxWireLegendDrawing(IReadOnlyList<DmxMarker> markers, IReadOnlyList<DmxNote> notes)
        {
            Markers = markers;
            Notes = notes;
        }

        /// <summary>The circled legend numbers, one per legend row (the <see cref="Marker"/> family).</summary>
        public IReadOnlyList<DmxMarker> Markers { get; }

        /// <summary>The title note plus one label note per row.</summary>
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

            // Title at the top, left-aligned over the number column.
            notes.Add(new DmxNote(new XY(markerX, 0.0), DmxOneLineGeometry.Legend.Title, DmxTextAlign.Left));

            double y = -DmxOneLineGeometry.Legend.TitleGap;
            foreach (var entry in legend.Entries)
            {
                markers.Add(new DmxMarker(new XY(markerX, y), entry.Type, entry.Number));
                notes.Add(new DmxNote(new XY(labelX, y), entry.Label, DmxTextAlign.Left));
                y -= DmxOneLineGeometry.Legend.RowPitch;
            }

            return new DmxWireLegendDrawing(markers, notes);
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
