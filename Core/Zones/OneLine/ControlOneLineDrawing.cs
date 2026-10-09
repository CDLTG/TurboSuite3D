#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>Horizontal alignment for a generator-drawn <see cref="ControlNote"/>.</summary>
    public enum ControlTextAlign { Left, Center, Right }

    /// <summary>Vertical alignment for a generator-drawn <see cref="ControlNote"/> relative to its
    /// insertion point. <see cref="Top"/> is the renderer default (text hangs below the point); the
    /// located-list rows use <see cref="Middle"/> so the text centers on the point its glyph sits on.</summary>
    public enum ControlVerticalAlign { Top, Middle, Bottom }

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
    /// A shade panel node (QSPS-10PNL) — same width as a power panel, shorter, bottom-aligned on the QS link.
    /// Labeled with its part number + <see cref="FillText"/> (e.g. "9/10"); one motor leg rises from the TOP
    /// labeled <c>n MOTORS</c> (v1 single stub — the <see cref="ControlOneLineGeometry.ShadePanel.MotorTap"/>
    /// anchor v2 fans out per motor). <see cref="MotorCount"/> is carried so the v2 fan is additive.
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

    /// <summary>
    /// A hybrid-repeater stamp (<c>ControlRepeaterDetail</c>) on a Clear Connect link (F4) — up to four chain
    /// along the link's solid QS leg. Carries only its catalog <see cref="PartNumber"/> (a repeater is not a
    /// located panel, so no name/fill). <see cref="Center"/> is the band center; the renderer places it with
    /// <c>PlaceFamilyGrowUp</c> (bottom-center origin, art grows up), like the shade node. The per-keypad RF
    /// fan off each repeater is deferred — the keypads keep the WIRELESS KEYPADS stub.
    /// </summary>
    public sealed class ControlRepeaterNode
    {
        public ControlRepeaterNode(XY center, string partNumber)
        {
            Center = center;
            PartNumber = partNumber;
        }

        public XY Center { get; }
        public string PartNumber { get; }
    }

    /// <summary>One drawn wire segment (a <c>DetailCurve</c>): endpoints + solid/dashed, per the Lutron
    /// line-style convention — <b>dashed = RF (wireless), solid = WIRED</b>. The wire's cable TYPE is carried by
    /// its circled marker number, not the style, so the style is free to mean wired-vs-RF.</summary>
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

        /// <summary>Dashed = an RF (wireless) connection; solid = a WIRED run (QS, CAT6, shade link, 120 V —
        /// every cable). The repeater→keypad RF tail is the only dashed segment, and it is deferred to the
        /// keypad-location expansion, so nothing is dashed today — every drawn segment is solid.</summary>
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
        public ControlNote(XY position, string text, ControlTextAlign align, double? textHeightFt = null,
            string? textTypeName = null, ControlVerticalAlign vAlign = ControlVerticalAlign.Top)
        {
            Position = position;
            Text = text;
            Align = align;
            TextHeightFt = textHeightFt;
            TextTypeName = textTypeName;
            VAlign = vAlign;
        }

        public XY Position { get; }
        public string Text { get; }
        public ControlTextAlign Align { get; }

        /// <summary>Vertical alignment relative to <see cref="Position"/>; Top is the default so existing
        /// notes are unchanged.</summary>
        public ControlVerticalAlign VAlign { get; }

        /// <summary>Paper text height override (feet); null ⇒ the renderer's default note type (1/16").</summary>
        public double? TextHeightFt { get; }

        /// <summary>Named firm text style to render this note in (e.g. AL_Annotation_4.5"); null ⇒ the default
        /// note type. Resolved by name in the shim; falls back to the default when the project lacks it.</summary>
        public string? TextTypeName { get; }
    }

    /// <summary>
    /// One page of the control one-line, as a pure, Revit-free set of primitives in model feet: the panel +
    /// shade nodes, the wire segments, the wire-type markers, and the native notes. The shim renderer wipes this
    /// page's owned Drafting View and replays it, so the drawing is regenerated from the snapshot every run
    /// (never hand-edited). One drawing per 42×30 page; the common case is a single page
    /// (<see cref="PageCount"/> == 1). There are no cross-page continuation glyphs: every tie is enclosure-local
    /// and an enclosure is never split across pages (the structural goal is a processor enclosure contained on
    /// one sheet), so no wire is ever cut by a page boundary.
    /// </summary>
    public sealed class ControlOneLineDrawing
    {
        public ControlOneLineDrawing(int pageIndex, int pageCount,
            IReadOnlyList<ControlPanelNode> panels,
            IReadOnlyList<ControlShadeNode> shades,
            IReadOnlyList<ControlWireSegment> wires,
            IReadOnlyList<ControlMarker> markers,
            IReadOnlyList<ControlNote> notes,
            IReadOnlyList<ControlRepeaterNode>? repeaters = null)
        {
            PageIndex = pageIndex;
            PageCount = pageCount;
            Panels = panels;
            Shades = shades;
            Wires = wires;
            Markers = markers;
            Notes = notes;
            Repeaters = repeaters ?? System.Array.Empty<ControlRepeaterNode>();
        }

        /// <summary>1-based page number — the stable key the owned view is registered under.</summary>
        public int PageIndex { get; }

        /// <summary>Total pages in this job, for the titleblock's "Sheet i of N".</summary>
        public int PageCount { get; }

        public IReadOnlyList<ControlPanelNode> Panels { get; }
        public IReadOnlyList<ControlShadeNode> Shades { get; }

        /// <summary>The hybrid-repeater stamps on this page's Clear Connect links (F4). Empty on a page with
        /// no wireless.</summary>
        public IReadOnlyList<ControlRepeaterNode> Repeaters { get; }

        public IReadOnlyList<ControlWireSegment> Wires { get; }
        public IReadOnlyList<ControlMarker> Markers { get; }
        public IReadOnlyList<ControlNote> Notes { get; }

        /// <summary>Deterministic owned-view name — a re-run finds + wipes this page's view by index (a stable
        /// key, unlike processor identity). e.g. "TurboControl One-Line - Sheet 1".</summary>
        public string ViewName(string systemName) => $"{systemName} One-Line - Sheet {PageIndex}";
    }
}
