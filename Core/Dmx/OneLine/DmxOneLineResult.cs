#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.OneLine
{
    /// <summary>Outcome of drawing one loop's one-line into its owned Drafting View,
    /// surfaced back to the window. <see cref="ViewId"/> is the owned view (created or re-used) the
    /// ViewModel persists so the next run finds and wipes the same view.</summary>
    public sealed class DmxOneLineResult
    {
        public int InterfaceNumber { get; set; }

        /// <summary>The owned Drafting View's element id (the Revit-free long). 0 ⇒ the draw failed.</summary>
        public long ViewId { get; set; }

        /// <summary>True when this run created the view; false when it re-used + wiped an existing one.</summary>
        public bool Created { get; set; }

        public int Symbols { get; set; }
        public int Wires { get; set; }
        public int Notes { get; set; }
        public int Markers { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        public bool Ok => ViewId != 0L;

        public string Summary =>
            (Ok ? (Created ? "Drew " : "Redrew ") : "Failed to draw ")
            + $"one-line for interface #{InterfaceNumber}"
            + (Ok ? $": {Symbols} symbol(s), {Wires} wire(s), {Markers} marker(s), {Notes} note(s)" : "")
            + (Warnings.Count > 0 ? $" ({Warnings.Count} warning(s))" : "") + ".";
    }
}
