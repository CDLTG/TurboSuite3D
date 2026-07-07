#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx.OneLine
{
    /// <summary>The five drafted box symbols (Detail Items). Maps to a family/type in <see cref="DmxOneLineGeometry"/>.</summary>
    public enum DmxSymbolKind { Decoder, Driver, Interface, Processor, Terminator }

    /// <summary>The canonical wire-type families a job draws (BuildPlan Phase 6). The first three are always
    /// present and always numbered 1–2–3; low-voltage homeruns are <c>#16-N</c> for the computed conductor
    /// count and are numbered <b>per-job</b> (4+, ascending by N — see <see cref="DmxWireLegend"/>).
    /// <see cref="WallCord"/> is smart-fixture only (deferred, never drawn today).</summary>
    public enum DmxWireCategory
    {
        LineVoltage,   // #12-2 Line Voltage — 120 V breaker→driver feed AND the driver-to-driver daisy
        Cat6,          // CAT6 Network Cable — the DMX daisy chain
        Comm,          // Control Communication Wire — interface↔processor
        LowVoltage,    // #16-N Stranded Low Voltage — driver→decoder jumper (N=2) and the tape homeruns
        WallCord,      // manufacturer wall-plug cord (smart fixtures, deferred)
    }

    /// <summary>
    /// One wire type on the one-line (BuildPlan Phase 6). Replaces the old fixed <c>Lv2/Lv4/Lv6</c> enum
    /// buckets: low-voltage carries its actual conductor count (<c>#16-N</c>, uncapped — 2, 4, 6, 8, …) so a
    /// 6-channel RGBATW tape correctly reads <c>#16-8</c> rather than the under-spec'd <c>#16-6</c> ceiling.
    /// Value type with structural equality so distinct <c>#16-N</c> sizes are distinct legend rows.
    /// </summary>
    public readonly struct DmxWireType : IEquatable<DmxWireType>
    {
        private DmxWireType(DmxWireCategory category, int conductors)
        {
            Category = category;
            Conductors = conductors;
        }

        public DmxWireCategory Category { get; }

        /// <summary>Conductor count — meaningful only for <see cref="DmxWireCategory.LowVoltage"/> (the N in
        /// <c>#16-N</c>); 0 for the fixed categories.</summary>
        public int Conductors { get; }

        public static DmxWireType Hv => new DmxWireType(DmxWireCategory.LineVoltage, 0);
        public static DmxWireType Cat6 => new DmxWireType(DmxWireCategory.Cat6, 0);
        public static DmxWireType Comm => new DmxWireType(DmxWireCategory.Comm, 0);
        public static DmxWireType WallCord => new DmxWireType(DmxWireCategory.WallCord, 0);

        /// <summary>A <c>#16-N</c> stranded low-voltage cable carrying <paramref name="conductors"/> conductors.</summary>
        public static DmxWireType Lv(int conductors) => new DmxWireType(DmxWireCategory.LowVoltage, conductors);

        /// <summary>The full legend label (matches the firm's legend sample, <c>Specs/_DMX/Legend.txt</c>).</summary>
        public string Label => Category switch
        {
            DmxWireCategory.LineVoltage => "#12-2 Line Voltage",
            DmxWireCategory.Cat6 => "CAT6 Network Cable",
            DmxWireCategory.Comm => "Control Communication Wire",
            DmxWireCategory.LowVoltage => $"#16-{Conductors} Stranded Low Voltage",
            DmxWireCategory.WallCord => "Wall Cord",
            _ => "",
        };

        /// <summary>The short gauge tag stamped beside a homerun (e.g. <c>#16-6</c>, <c>#12-2</c>).</summary>
        public string Gauge => Category switch
        {
            DmxWireCategory.LineVoltage => "#12-2",
            DmxWireCategory.Cat6 => "CAT6",
            DmxWireCategory.Comm => "COMM",
            DmxWireCategory.LowVoltage => $"#16-{Conductors}",
            _ => "",
        };

        public bool Equals(DmxWireType other) => Category == other.Category && Conductors == other.Conductors;
        public override bool Equals(object? obj) => obj is DmxWireType o && Equals(o);
        public override int GetHashCode() => ((int)Category * 397) ^ Conductors;
        public static bool operator ==(DmxWireType a, DmxWireType b) => a.Equals(b);
        public static bool operator !=(DmxWireType a, DmxWireType b) => !a.Equals(b);
        public override string ToString() => Label;
    }

    /// <summary>One row of the generated wire legend: its job number + the wire type it names.</summary>
    public sealed class DmxWireLegendEntry
    {
        public DmxWireLegendEntry(int number, DmxWireType type)
        {
            Number = number;
            Type = type;
        }

        public int Number { get; }
        public DmxWireType Type { get; }
        public string Label => Type.Label;

        /// <summary>"1  #16-2 Stranded Low Voltage" — the legend line.</summary>
        public override string ToString() => $"{Number}  {Label}";
    }

    /// <summary>
    /// The per-job wire legend (BuildPlan Phase 6). The firm numbers wire types <b>densely and per-job</b> —
    /// only the types actually used appear, numbered sequentially in a fixed canonical order so an unused
    /// size is skipped (their sample: <c>#16-4</c> absent ⇒ <c>#16-6</c> gets 5, not 6). The same number is
    /// stamped on every wire of that type (the <c>WireMark</c> annotation) AND emitted into the legend, so
    /// number↔type is exactly 1:1 within a job. Built once from the solved bill and shared across every loop.
    /// </summary>
    public sealed class DmxWireLegend
    {
        private readonly Dictionary<DmxWireType, int> _numbers;

        private DmxWireLegend(IReadOnlyList<DmxWireLegendEntry> entries)
        {
            Entries = entries;
            _numbers = entries.ToDictionary(e => e.Type, e => e.Number);
        }

        /// <summary>The legend rows, in canonical order (Line Voltage, CAT6, Comm, then #16-N ascending).</summary>
        public IReadOnlyList<DmxWireLegendEntry> Entries { get; }

        /// <summary>The job number for a wire type; 0 if the type isn't in this job's legend.</summary>
        public int NumberFor(DmxWireType type) => _numbers.TryGetValue(type, out int n) ? n : 0;

        /// <summary>The conductor count for one zone's homerun = channels + 1 common, rounded up to the next
        /// even stock size, then bumped <paramref name="pullUpSizes"/> stock sizes (job-wide pull-up). Uncapped
        /// — 2, 4, 6, 8, … as the channel count demands.</summary>
        public static int HomerunConductors(int channels, int pullUpSizes)
        {
            int n = WireSpec.StockConductors(channels);
            for (int i = 0; i < pullUpSizes && i >= 0; i++) n += 2;
            return n;
        }

        /// <summary>The <c>#16-N</c> homerun wire type for a zone's channel count (with the job pull-up).</summary>
        public static DmxWireType HomerunFor(int channels, int pullUpSizes = 0)
            => DmxWireType.Lv(HomerunConductors(channels, pullUpSizes));

        /// <summary>Build the legend from the wire types a job actually uses. The three fixed categories are
        /// always emitted as 1–2–3 (decision 2026-06-29); distinct low-voltage conductor counts follow,
        /// ascending, starting at 4. Duplicate types collapse to one row.</summary>
        public static DmxWireLegend Build(IEnumerable<DmxWireType> usedTypes)
        {
            var entries = new List<DmxWireLegendEntry>
            {
                new DmxWireLegendEntry(1, DmxWireType.Hv),
                new DmxWireLegendEntry(2, DmxWireType.Cat6),
                new DmxWireLegendEntry(3, DmxWireType.Comm),
            };

            var lvCounts = (usedTypes ?? Enumerable.Empty<DmxWireType>())
                .Where(t => t.Category == DmxWireCategory.LowVoltage)
                .Select(t => t.Conductors)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            int next = 4;
            foreach (int n in lvCounts)
                entries.Add(new DmxWireLegendEntry(next++, DmxWireType.Lv(n)));

            return new DmxWireLegend(entries);
        }

        /// <summary>Build the job legend straight off a solved bill: every zone's homerun gauge (with pull-up)
        /// plus the always-present <c>#16-2</c> driver→decoder jumper (shared with a 1-channel homerun).</summary>
        public static DmxWireLegend ForBill(DmxBill bill, int pullUpSizes = 0)
        {
            var used = new List<DmxWireType>();
            if (bill != null)
            {
                bool anyDecoder = false;
                foreach (var zone in bill.Zones)
                {
                    if (zone.DecoderCount <= 0) continue;
                    anyDecoder = true;
                    used.Add(HomerunFor(zone.Channels, pullUpSizes));
                }
                // The 24 V driver→decoder jumper is #16-2, present wherever a decoder is — and it shares its
                // number with a 1-channel homerun (BuildPlan Phase 6).
                if (anyDecoder) used.Add(DmxWireType.Lv(2));
            }
            return Build(used);
        }
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

        /// <summary>Dashed = the control wires (DMX chain + interface↔processor comm), which the renderer draws
        /// with the "Wiring (CAT6)" line style; solid = the power wires, drawn "Wiring".</summary>
        public bool Dashed { get; }
    }

    /// <summary>One wire-type marker (the Generic Annotation circled number) placed ON a wire. The
    /// <see cref="Number"/> is the per-job legend number resolved at plan time (BuildPlan Phase 6).</summary>
    public sealed class DmxMarker
    {
        public DmxMarker(XY position, DmxWireType type, int number)
        {
            Position = position;
            Type = type;
            Number = number;
        }

        public XY Position { get; }
        public DmxWireType Type { get; }
        public int Number { get; }
        public string Mark => Number.ToString();
    }

    public enum DmxTextAlign { Left, Center, Right }

    /// <summary>One native <c>TextNote</c> the generator draws (leaders/headers, 1/16").</summary>
    public sealed class DmxNote
    {
        public DmxNote(XY position, string text, DmxTextAlign align, double? textHeightFt = null)
        {
            Position = position;
            Text = text;
            Align = align;
            TextHeightFt = textHeightFt;
        }

        public XY Position { get; }
        public string Text { get; }
        public DmxTextAlign Align { get; }

        /// <summary>Paper text height override (feet); null ⇒ the renderer's default note type (1/16").</summary>
        public double? TextHeightFt { get; }
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
