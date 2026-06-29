#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.OneLine
{
    /// <summary>The five drafted box symbols (Detail Items). Maps to a family/type in <see cref="DmxOneLineGeometry"/>.</summary>
    public enum DmxSymbolKind { Decoder, Driver, Interface, Processor, Terminator }

    /// <summary>The wire legend (Screenshot_196). The value IS the circled marker number the generator
    /// writes to the wire-mark annotation's <c>WireMark</c> param. ⑤ #16-6 is the LV homerun ceiling we
    /// draw (RGBW); ② wall cord is smart-fixture only (deferred).</summary>
    public enum DmxWireType
    {
        Hv = 1,        // #12-2 romex — 120 V breaker→driver feed AND the driver-to-driver daisy
        WallCord = 2,  // manufacturer wall-plug cord (smart fixtures, deferred)
        Lv2 = 3,       // #16-2 — driver→decoder (24 V), and a 1-channel homerun
        Lv4 = 4,       // #16-4 — TW / RGB homerun (2–3 ch)
        Lv6 = 5,       // #16-6 — RGBW homerun (4–5 ch)
        Cat6 = 6,      // CAT6 — the DMX daisy chain
        Comm = 7,      // Lutron comm — interface↔processor
    }

    /// <summary>Legend lookups shared by the planner (and re-usable by the author-once legend view).</summary>
    public static class DmxWireLegend
    {
        /// <summary>The homerun gauge by zone channel count → legend #: 1ch ⇒ ③, 2–3 ⇒ ④, ≥4 ⇒ ⑤ (RGBW ceiling).</summary>
        public static DmxWireType HomerunFor(int channels)
        {
            int conductors = WireSpec.StockConductors(channels);   // channels + 1 common, rounded to even
            if (conductors <= 2) return DmxWireType.Lv2;
            if (conductors <= 4) return DmxWireType.Lv4;
            return DmxWireType.Lv6;
        }

        /// <summary>The marker string the annotation carries ("1".."7").</summary>
        public static string Mark(DmxWireType t) => ((int)t).ToString();
    }

    /// <summary>One placed box symbol: its kind/family/type, center position (model feet), and the instance
    /// label params the renderer writes (e.g. <c>DecNumber</c>→"DEC 20").</summary>
    public sealed class DmxSymbolInstance
    {
        public DmxSymbolInstance(DmxSymbolKind kind, string family, string type, XY position,
                                 IReadOnlyDictionary<string, string> @params)
        {
            Kind = kind;
            Family = family;
            Type = type;
            Position = position;
            Params = @params;
        }

        public DmxSymbolKind Kind { get; }
        public string Family { get; }
        public string Type { get; }
        public XY Position { get; }
        public IReadOnlyDictionary<string, string> Params { get; }
    }

    /// <summary>One drawn wire segment (a <c>DetailCurve</c>): endpoints + solid/dashed. Power = solid,
    /// control (DMX/comm) = dashed.</summary>
    public sealed class DmxWireSegment
    {
        public DmxWireSegment(XY start, XY end, bool dashed)
        {
            Start = start;
            End = end;
            Dashed = dashed;
        }

        public XY Start { get; }
        public XY End { get; }
        public bool Dashed { get; }
    }

    /// <summary>One wire-type marker (the Generic Annotation circled number) placed ON a wire.</summary>
    public sealed class DmxMarker
    {
        public DmxMarker(XY position, DmxWireType type)
        {
            Position = position;
            Type = type;
        }

        public XY Position { get; }
        public DmxWireType Type { get; }
        public string Mark => DmxWireLegend.Mark(Type);
    }

    public enum DmxTextAlign { Left, Center, Right }

    /// <summary>One native <c>TextNote</c> the generator draws (leaders/headers, 1/16").</summary>
    public sealed class DmxNote
    {
        public DmxNote(XY position, string text, DmxTextAlign align)
        {
            Position = position;
            Text = text;
            Align = align;
        }

        public XY Position { get; }
        public string Text { get; }
        public DmxTextAlign Align { get; }
    }

    /// <summary>
    /// One loop's complete one-line, as a pure, Revit-free set of primitives (BuildPlan Phase 4): the box
    /// symbols + their label params, the wire segments, the wire-type markers, and the native notes — all
    /// in model feet. The shim renderer wipes the loop's owned Drafting View and replays this, so the
    /// drawing is regenerated from the snapshot every run (never hand-edited). One drawing per loop.
    /// </summary>
    public sealed class DmxOneLineDrawing
    {
        public DmxOneLineDrawing(int interfaceNumber, string? loopName,
                                 IReadOnlyList<DmxSymbolInstance> symbols,
                                 IReadOnlyList<DmxWireSegment> wires,
                                 IReadOnlyList<DmxMarker> markers,
                                 IReadOnlyList<DmxNote> notes)
        {
            InterfaceNumber = interfaceNumber;
            LoopName = loopName;
            Symbols = symbols;
            Wires = wires;
            Markers = markers;
            Notes = notes;
        }

        public int InterfaceNumber { get; }
        public string? LoopName { get; }
        public IReadOnlyList<DmxSymbolInstance> Symbols { get; }
        public IReadOnlyList<DmxWireSegment> Wires { get; }
        public IReadOnlyList<DmxMarker> Markers { get; }
        public IReadOnlyList<DmxNote> Notes { get; }

        /// <summary>Deterministic owned-view name (BuildPlan Phase 4): a re-run finds + wipes this view.</summary>
        public string ViewName(string systemName) =>
            $"TurboDMX — {systemName} — Interface #{InterfaceNumber}";
    }
}
