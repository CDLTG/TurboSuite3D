#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>Outcome of drawing ONE page of the control one-line into its owned Drafting View, surfaced
    /// back to the window. Mirrors <c>DmxOneLineResult</c>, keyed by <see cref="PageIndex"/> (the stable
    /// owned-view key) instead of an interface number. <see cref="ViewId"/> is the owned view (created or
    /// re-used) the ViewModel persists so the next run finds and wipes the same view; the service returns one
    /// of these per page (plus the wire-legend view's own result).</summary>
    public sealed class ControlOneLineResult
    {
        /// <summary>1-based page number this result is for — the key the owned view is registered under.</summary>
        public int PageIndex { get; set; }

        /// <summary>The owned Drafting View's element id (the Revit-free long). 0 ⇒ the draw failed.</summary>
        public long ViewId { get; set; }

        /// <summary>True when this run created the view; false when it re-used + wiped an existing one.</summary>
        public bool Created { get; set; }

        public int Panels { get; set; }
        public int Shades { get; set; }
        public int Symbols { get; set; }
        public int Wires { get; set; }
        public int Notes { get; set; }
        public int Markers { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        public bool Ok => ViewId != 0L;

        public string Summary =>
            (Ok ? (Created ? "Drew " : "Redrew ") : "Failed to draw ")
            + $"one-line sheet {PageIndex}"
            + (Ok ? $": {Panels} panel(s), {Shades} shade(s), {Wires} wire(s), {Markers} marker(s), {Notes} note(s)" : "")
            + (Warnings.Count > 0 ? $" ({Warnings.Count} warning(s))" : "") + ".";
    }
}
