#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>Horizontal alignment for a generator-drawn <see cref="ControlNote"/>.</summary>
    public enum ControlTextAlign { Left, Center, Right }

    /// <summary>
    /// The wire types the control one-line draws + names in its legend. Mirrors <c>DmxWireType</c>, but a
    /// plain enum — control wires have no conductor-count variants. Presence + numbering + labels live in
    /// <c>ControlWireLegend</c> (built next, #5); this is only the identity a <see cref="ControlWireSegment"/>
    /// and its <see cref="ControlMarker"/> carry. <b>Open (plan):</b> whether the firm distinguishes a
    /// <see cref="PanelControlLink"/> from the plain <see cref="QsControlLink"/> — confirm before the legend
    /// roster is finalized; kept here so the model is ready either way.
    /// </summary>
    public enum ControlWireType
    {
        QsControlLink,      // the QS daisy — the spine each processor link runs
        PanelControlLink,   // OPEN: a distinct panel-to-panel control link, if the firm separates it from QS
        ClearConnect,       // RF / Clear Connect Type A — the wireless leg
        ShadeLink,          // QS Sivoia shade link — shade panel to its motors
        Input120V,          // 120 V feed into a panel
        Cat6,               // CAT6 network — processor ↔ HOME NETWORK, and inter-processor comm
        DaliLoop            // DALI loop — present only when a DALI subsystem is
    }

    /// <summary>The simple authored/annotation glyphs that are not panels, shades, notes, wires or markers.
    /// Panels and shades are their own rich node records (renderer-drawn); this covers the shared head-end
    /// glyph. The processor is NOT here — it folds into its panel as a compartment tile.</summary>
    public enum ControlSymbolKind { HomeNetwork }

    /// <summary>
    /// A power panel node (dimmer OR processor-hosting — one enclosure size), rendered as the Panel-Breakdown
    /// tile stack: <see cref="ModuleTiles"/> top→bottom, then the processor compartment tile when
    /// <see cref="ProcessorPartNumber"/> is set, then a footer of <see cref="Name"/> + <see cref="FillText"/>
    /// + <see cref="PartNumber"/>. Each tile carries its module CATALOG PART NUMBER only (no type/fraction/
    /// color); a null tile is an empty slot. The renderer computes tile geometry from
    /// <see cref="ControlOneLineGeometry.Panel"/> (height derives from the tile count), so this record is
    /// pure content, not layout.
    /// </summary>
    public sealed class ControlPanelNode
    {
        public ControlPanelNode(XY center, string name, string partNumber, string fillText,
            IReadOnlyList<string?> moduleTiles, IReadOnlyList<string> lvSlots, bool hostsProcessor,
            string enclosureRole)
        {
            Center = center;
            Name = name;
            PartNumber = partNumber;
            FillText = fillText;
            ModuleTiles = moduleTiles;
            LvSlots = lvSlots;
            HostsProcessor = hostsProcessor;
            EnclosureRole = enclosureRole;
        }

        public XY Center { get; }

        /// <summary>Panel name shown in the footer (e.g. "1-A").</summary>
        public string Name { get; }

        /// <summary>Panel catalog part number shown in the footer (e.g. "PD8-59F-120").</summary>
        public string PartNumber { get; }

        /// <summary>Footer fill total (e.g. "8/8").</summary>
        public string FillText { get; }

        /// <summary>One entry per module slot, top→bottom: the module's catalog part number, or null = empty.</summary>
        public IReadOnlyList<string?> ModuleTiles { get; }

        /// <summary>The LV-compartment labels, top→bottom (PD8/PD9 = 1, LV21 = 2): a processor part number,
        /// "EMPTY", or an IO/interface device. Each drawn with the shared LV-slot tile family.</summary>
        public IReadOnlyList<string> LvSlots { get; }

        /// <summary>True when this panel hosts the processor — the planner draws it as the head of its links.</summary>
        public bool HostsProcessor { get; }

        /// <summary>TurboSuite Role of the enclosure family to place (branded: PD8/PD9, LV21, …).</summary>
        public string EnclosureRole { get; }
    }

    /// <summary>
    /// A shade panel node (QSPS-10PNL) — the smaller external enclosure. Labeled with its part number +
    /// <see cref="FillText"/> (e.g. "9/10"); one motor leg drops from the bottom labeled <c>n MOTORS</c>
    /// (v1 single stub — the <see cref="ControlOneLineGeometry.ShadePanel.MotorDrop"/> anchor v2 fans out
    /// per motor). <see cref="MotorCount"/> is carried so the v2 fan is additive.
    /// </summary>
    public sealed class ControlShadeNode
    {
        public ControlShadeNode(XY center, string name, string partNumber, string fillText, int motorCount)
        {
            Center = center;
            Name = name;
            PartNumber = partNumber;
            FillText = fillText;
            MotorCount = motorCount;
        }

        public XY Center { get; }
        public string Name { get; }
        public string PartNumber { get; }
        public string FillText { get; }
        public int MotorCount { get; }
    }

    /// <summary>One placed simple glyph: its kind, center position (model feet), and the instance label
    /// params the renderer writes. The renderer resolves the family by the kind's TurboSuite Role.</summary>
    public sealed class ControlSymbolInstance
    {
        public ControlSymbolInstance(ControlSymbolKind kind, XY position,
            IReadOnlyDictionary<string, string> @params)
        {
            Kind = kind;
            Position = position;
            Params = @params;
        }

        public ControlSymbolKind Kind { get; }
        public XY Position { get; }
        public IReadOnlyDictionary<string, string> Params { get; }
    }

    /// <summary>One drawn wire segment (a <c>DetailCurve</c>): endpoints + solid/dashed. Power = solid,
    /// control (QS/CAT6/Clear Connect) = dashed.</summary>
    public sealed class ControlWireSegment
    {
        public ControlWireSegment(XY start, XY end, bool dashed)
        {
            Start = start;
            End = end;
            Dashed = dashed;
        }

        public XY Start { get; }
        public XY End { get; }

        /// <summary>Dashed = the control wires (QS spine, CAT6, Clear Connect), drawn with the dashed line
        /// style; solid = power (120 V feeds), drawn "Wiring".</summary>
        public bool Dashed { get; }
    }

    /// <summary>One wire-type marker (the circled-number Generic Annotation) placed ON a wire. The
    /// <see cref="Number"/> is the per-job legend number resolved at plan time.</summary>
    public sealed class ControlMarker
    {
        public ControlMarker(XY position, ControlWireType type, int number)
        {
            Position = position;
            Type = type;
            Number = number;
        }

        public XY Position { get; }
        public ControlWireType Type { get; }
        public int Number { get; }
        public string Mark => Number.ToString();
    }

    /// <summary>One native <c>TextNote</c> the generator draws (leaders/headers/stubs, 1/16" by default).</summary>
    public sealed class ControlNote
    {
        public ControlNote(XY position, string text, ControlTextAlign align, double? textHeightFt = null)
        {
            Position = position;
            Text = text;
            Align = align;
            TextHeightFt = textHeightFt;
        }

        public XY Position { get; }
        public string Text { get; }
        public ControlTextAlign Align { get; }

        /// <summary>Paper text height override (feet); null ⇒ the renderer's default note type (1/16").</summary>
        public double? TextHeightFt { get; }
    }

    /// <summary>A cross-page continuation glyph — the "To Sheet N" / "From Sheet N" bubble where a wire is
    /// cut by the page boundary. Stage-2 (pagination) only; the stage-1 single-page planner emits none.</summary>
    public sealed class ControlContinuation
    {
        public ControlContinuation(XY position, int otherSheet, bool incoming)
        {
            Position = position;
            OtherSheet = otherSheet;
            Incoming = incoming;
        }

        public XY Position { get; }

        /// <summary>The sheet this wire continues to (outgoing) or from (incoming).</summary>
        public int OtherSheet { get; }

        /// <summary>True = this bubble receives a wire from another sheet ("FROM SHEET n"); false = it sends
        /// one on ("TO SHEET n").</summary>
        public bool Incoming { get; }
    }

    /// <summary>
    /// One page of the control one-line, as a pure, Revit-free set of primitives in model feet: the panel +
    /// shade nodes, the simple glyphs, the wire segments, the wire-type markers, the native notes, and any
    /// cross-page continuation bubbles. The shim renderer wipes this page's owned Drafting View and replays
    /// it, so the drawing is regenerated from the snapshot every run (never hand-edited). One drawing per
    /// 42×30 page; the common case is a single page (<see cref="PageCount"/> == 1).
    /// </summary>
    public sealed class ControlOneLineDrawing
    {
        public ControlOneLineDrawing(int pageIndex, int pageCount,
            IReadOnlyList<ControlPanelNode> panels,
            IReadOnlyList<ControlShadeNode> shades,
            IReadOnlyList<ControlSymbolInstance> symbols,
            IReadOnlyList<ControlWireSegment> wires,
            IReadOnlyList<ControlMarker> markers,
            IReadOnlyList<ControlNote> notes,
            IReadOnlyList<ControlContinuation> continuations)
        {
            PageIndex = pageIndex;
            PageCount = pageCount;
            Panels = panels;
            Shades = shades;
            Symbols = symbols;
            Wires = wires;
            Markers = markers;
            Notes = notes;
            Continuations = continuations;
        }

        /// <summary>1-based page number — the stable key the owned view is registered under.</summary>
        public int PageIndex { get; }

        /// <summary>Total pages in this job, for the titleblock's "Sheet i of N".</summary>
        public int PageCount { get; }

        public IReadOnlyList<ControlPanelNode> Panels { get; }
        public IReadOnlyList<ControlShadeNode> Shades { get; }
        public IReadOnlyList<ControlSymbolInstance> Symbols { get; }
        public IReadOnlyList<ControlWireSegment> Wires { get; }
        public IReadOnlyList<ControlMarker> Markers { get; }
        public IReadOnlyList<ControlNote> Notes { get; }
        public IReadOnlyList<ControlContinuation> Continuations { get; }

        /// <summary>Deterministic owned-view name — a re-run finds + wipes this page's view by index (a stable
        /// key, unlike processor identity). e.g. "TurboControl One-Line - Sheet 1".</summary>
        public string ViewName(string systemName) => $"{systemName} One-Line - Sheet {PageIndex}";
    }
}
