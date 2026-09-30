#nullable enable
namespace TurboSuite.Zones.OneLine
{
    /// <summary>A 2D point/offset in <b>model feet</b> (Revit internal units). <see cref="In"/> builds one
    /// from inches so the spec below reads in the units the families/detail items are drawn in. Mirrors
    /// <c>TurboSuite.Dmx.OneLine.XY</c> (kept local so Zones has no dependency on the Dmx assembly area).</summary>
    public readonly struct XY
    {
        public XY(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }

        /// <summary>From inches → feet (e.g. <c>XY.In(-21, 0)</c> = 21" left of origin).</summary>
        public static XY In(double xInches, double yInches) => new XY(xInches / 12.0, yInches / 12.0);

        public XY Plus(XY o) => new XY(X + o.X, Y + o.Y);
        public XY Offset(double dx, double dy) => new XY(X + dx, Y + dy);
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>
    /// The SOURCE OF TRUTH for the Lutron control one-line's geometry — box sizes, the module-tile grid, the
    /// connection-point offsets the wires target, layout spacing, and the 42×30 page module. All lengths are
    /// <b>model feet</b>; nodes are drawn at architectural size and read at the pinned view scale. Pure data:
    /// the Core planner (<c>ControlOneLinePlanner</c>) and the shim renderer (<c>ControlOneLineService</c>)
    /// both read it so they cannot disagree. Modeled on <c>DmxOneLineGeometry</c>.
    ///
    /// <para><b>Shape (locked 2026-09-24, see the plan's Section-2 shape block):</b> horizontal — processor
    /// bays stack DOWN the left; each QS link runs LEFT→RIGHT off its processor panel with panel nodes hung
    /// along it and a keypad stub at the tail. The processor is NOT a separate box: it is the bottom
    /// COMPARTMENT tile of its power panel. All power panels are one enclosure width and are drawn as the
    /// Panel-Breakdown (584) tile-stack — a stack of module tiles, each carrying its catalog PART NUMBER only
    /// (no type, no per-tile fraction, no color), a footer of name + fill total + panel part number.
    /// Tiles are RENDERER-DRAWN (detail rectangles + <c>TextNote</c>s, variable count per panel), so this
    /// file gives the tile GRID, not family params.</para>
    ///
    /// <para><b>Numbers below are starting values to tune in-Revit</b> (like the DMX geometry was), against a
    /// real 42×30 titleblock and the authored panel-outline/shade/marker families. The STRUCTURE — which
    /// offsets exist and how heights are derived — is the part to get right first.</para>
    /// </summary>
    public static class ControlOneLineGeometry
    {
        /// <summary>Pinned drafting-view scale: 1/4" = 1'-0" ⇒ ratio 1:48. The renderer sets this on each page view.</summary>
        public const int ViewScale = 48;

        /// <summary>Paper text height for the generator's native notes (1/16"), expressed in feet.</summary>
        public const double NoteTextHeightFt = (1.0 / 16.0) / 12.0;

        /// <summary>Firm text style for prominent labels (HOME NETWORK, 120V) — larger than the generic note
        /// type. Resolved by NAME in the shim; falls back to the generic type when the project lacks it.</summary>
        public const string LargeTextTypeName = "AL_Annotation_4.5\"";

        /// <summary>Paper text height for a module tile's part number (1/16"). Tight against the tile height —
        /// tune together with <see cref="Panel.TileHeight"/>.</summary>
        public const double TileTextHeightFt = (1.0 / 16.0) / 12.0;

        /// <summary>Convert a paper length (inches) to the model feet it occupies at <see cref="ViewScale"/>.
        /// The 42×30 page rectangle is derived through this so pagination reasons in the same model space the
        /// drawing lives in.</summary>
        public static double PaperInchesToModelFt(double paperInches) => paperInches * ViewScale / 12.0;

        /// <summary>
        /// The 42×30 landscape page module (locked 2026-09-24). The planner fills one page top→bottom /
        /// left→right and BREAKS to the next when the running content would exceed the usable rectangle,
        /// dropping a <see cref="ContinuationBubble"/> where a QS link or the inter-processor CAT6 crosses.
        /// Each page is its own owned Drafting View, keyed by page index (Sheet 1..N — stable, unlike processor
        /// identity). The usable rectangle is the sheet minus its border margin and the titleblock strip,
        /// expressed in MODEL FEET (paper inches × <see cref="ViewScale"/>).
        /// </summary>
        public static class Page
        {
            public const double SheetPaperWidthIn = 42.0;
            public const double SheetPaperHeightIn = 30.0;

            /// <summary>Border/gutter inside the sheet edge (paper inches).</summary>
            public const double MarginPaperIn = 1.5;

            /// <summary>Right-edge titleblock + wire-legend strip reserved out of the usable width (paper inches).</summary>
            public const double TitleblockStripPaperIn = 6.0;

            /// <summary>Usable content width in MODEL FEET (links grow into this — the roomy axis).</summary>
            public static readonly double ContentWidthFt =
                ControlOneLineGeometry.PaperInchesToModelFt(SheetPaperWidthIn - 2 * MarginPaperIn - TitleblockStripPaperIn);

            /// <summary>Usable content height in MODEL FEET (the scarce axis — bays stack until this is spent, then paginate).</summary>
            public static readonly double ContentHeightFt =
                ControlOneLineGeometry.PaperInchesToModelFt(SheetPaperHeightIn - 2 * MarginPaperIn);
        }

        /// <summary>
        /// A power panel (dimmer OR processor-hosting — same enclosure). Drawn as a vertical tile stack:
        /// <c>[module tile]×N</c> then, when it hosts the processor, a bottom <c>[processor compartment]</c>
        /// tile, then a footer.
        ///
        /// <para><b>Fixed 9-rung enclosure (the real 59″ box). </b>All power panels draw at ONE height — 9
        /// rungs — even though <see cref="Height"/> takes counts. This falls out of the data model, not a
        /// special case here: <c>PanelResult.ModuleTiles.Count</c> always equals <c>PanelCapacity</c>
        /// (<c>EmptySlots</c> pads to it), so PD8 (cap 8 + 1 LV compartment) and PD9 (cap 9 + 0) both total 9,
        /// and <see cref="Height"/>(m, lv) returns the same value for every power panel. The **LV compartment is
        /// authored/rendered as the BOTTOM rung** (drawn at tile index after the modules), only on a PD8 — a PD9
        /// is 9 modules and never hosts a processor. <see cref="Height"/> is deliberately kept count-driven (NOT
        /// a flat constant) so the deferred LV21 — 0 modules + 2 LV rungs — gets its own smaller height.</para>
        ///
        /// <para><b>Origin = family BOTTOM-CENTER; artwork grows UP.</b> Families are authored asymmetric — the
        /// insertion origin sits at the bottom-center of the art, which expands upward from it. The renderer
        /// places each family at the BOTTOM of its band (band center − height/2, via
        /// <c>ControlOneLineService.PlaceFamilyGrowUp</c>), so the grown-up art fills the same band the tiles +
        /// fallback occupy. Layout math (planner + <see cref="TileCenterY"/>) still works in band CENTERS, and
        /// connection points are relative to the panel CENTER; because <see cref="Height"/> is uniform for power
        /// panels the vertical ones are stable, but they are still computed from <see cref="Height"/> via the
        /// helpers so the LV21 (different height) stays correct.</para>
        /// </summary>
        public static class Panel
        {
            /// <summary>The real 59″ enclosure's DIN-rung count. Documents the fixed-height invariant (a power
            /// panel always fills exactly this many rungs: modules + the bottom LV compartment on a PD8); it is
            /// not a divisor in <see cref="Height"/>, which stays count-driven so LV21 keeps its own height.</summary>
            public const int SlotCount = 9;

            /// <summary>LV21's rung count (2 LV rungs, no modules) — the low anchor for the linear
            /// height-per-rung fit in <see cref="Height"/>.</summary>
            private const int Lv21SlotCount = 2;

            public const double Width = (71.0 + 197.0 / 256.0) / 12.0;   // 5'-11 197/256" — measured authored family width

            // ── Measured outer heights of the authored enclosures (transcribed 2026-09-28) ──────────────
            // The power enclosure (9 rungs) and the LV21 (2 rungs) share one DIN-rung line, so their heights
            // are linear in rung count; Height() fits a line through these two points, reproducing each
            // exactly and yielding the shared rung pitch as a byproduct.
            public const double PowerPanelHeight = (272.0 + 61.0 / 64.0) / 12.0;    // 22'-8 61/64"  (9 rungs)
            public const double Lv21Height       = (93.0 + 103.0 / 256.0) / 12.0;   // 7'-9 103/256" (2 rungs)

            /// <summary>Rung pitch shared across the enclosure line, derived from the two measured heights.</summary>
            public static double RungPitch => (PowerPanelHeight - Lv21Height) / (SlotCount - Lv21SlotCount);

            /// <summary>The non-rung remainder of a full panel (header pad above rung 1 + footer band),
            /// derived so <see cref="Height"/> lands on the measured outer heights exactly.</summary>
            public static double NonRungHeight => PowerPanelHeight - SlotCount * RungPitch;

            // Header pad above the first rung + footer band, split from NonRungHeight on the original 3:15
            // proportion (interim — the footer/header split only shifts tile Ys, which the per-tile origin
            // offsets will supersede).
            public static double HeaderPad => NonRungHeight * (3.0 / 18.0);
            public static double FooterHeight => NonRungHeight * (15.0 / 18.0);

            // Tile art size is irrelevant to placement — each tile family anchors by its (bottom-center) ORIGIN
            // at a rung Y in RungOffsets; TileHeight is kept only for the LV21 interim fallback grid.
            public const double TileHeight = (21.0 + 15.0 / 32.0) / 12.0;   // 1'-9 15/32" (ControlModuleDetail)
            public const double TileInset = 3.0 / 12.0;     // tile-grid spec (side inset); not used by the renderer

            /// <summary>Rung anchor Ys for a power panel, measured UP from the family BOTTOM origin, BOTTOM RUNG
            /// FIRST (index 0 = rung 1 = the LV/processor compartment on a PD8, or module 1 on a PD9; index 8 =
            /// top). Transcribed from the authored ControlPanelDetail (2026-09-28). A module tile and the LV tile
            /// anchor to the SAME rung spot, so both are placed at <c>originY + RungOffsets[r]</c>. Renderer maps
            /// its top-down tile index <c>k</c> (of <see cref="SlotCount"/> total) to <c>RungOffsets[SlotCount-1-k]</c>.</summary>
            public static readonly double[] RungOffsets =
            {
                (42.0 + 7.0 / 8.0) / 12.0,        // rung 1 (bottom): 3'-6 7/8"
                (68.0 + 135.0 / 256.0) / 12.0,    // rung 2:          5'-8 135/256"
                (94.0 + 45.0 / 256.0) / 12.0,     // rung 3:          7'-10 45/256"
                (119.0 + 53.0 / 64.0) / 12.0,     // rung 4:          9'-11 53/64"
                (145.0 + 61.0 / 128.0) / 12.0,    // rung 5:          12'-1 61/128"
                (171.0 + 1.0 / 8.0) / 12.0,       // rung 6:          14'-3 1/8"
                (196.0 + 199.0 / 256.0) / 12.0,   // rung 7:          16'-4 199/256"
                (222.0 + 109.0 / 256.0) / 12.0,   // rung 8:          18'-6 109/256"
                (248.0 + 5.0 / 64.0) / 12.0,      // rung 9 (top):    20'-8 5/64"
            };

            /// <summary>LV21 rung anchor Ys, BOTTOM-UP (index 0 = bottom rung, index 1 = top), measured UP from the
            /// family BOTTOM origin — the LV21's own 2-rung equivalent of <see cref="RungOffsets"/>. The bottom rung
            /// shares the power panel's rung-1 offset (3'-6 7/8") so a processor tile sits at the same height in
            /// either enclosure; the top rung is authored at 6'-6 47/64". Transcribed from the authored
            /// ControlLv21Detail (2026-09-29). The renderer picks this vs <see cref="RungOffsets"/> by tile count.</summary>
            public static readonly double[] Lv21RungOffsets =
            {
                (42.0 + 7.0 / 8.0) / 12.0,        // rung 1 (bottom): 3'-6 7/8"   (== power panel rung 1)
                (78.0 + 47.0 / 64.0) / 12.0,      // rung 2 (top):    6'-6 47/64"
            };

            // Footer label params the renderer writes on the enclosure family (ControlPanelDetail /
            // ControlLv21Detail); the family author names its text params to match. Tiles are SEPARATE family
            // placements — see the sibling <see cref="Module"/> / <see cref="LvSlot"/> classes.
            public const string NameParam = "PanelName";
            public const string FillParam = "Fill";
            public const string PartNumberParam = "PartNumber";

            public static double TileWidth => Width - 2 * TileInset;
            public static double TilePitch => RungPitch;

            /// <summary>Total drawn height for a panel with <paramref name="moduleCount"/> module tiles plus
            /// <paramref name="lvSlotCount"/> LV-compartment tiles (PD8/PD9 = 1, LV21 = 2, shade panels = 0).</summary>
            public static double Height(int moduleCount, int lvSlotCount)
            {
                int slots = moduleCount + lvSlotCount;
                return HeaderPad + slots * TilePitch + FooterHeight;
            }

            /// <summary>Center-Y of tile <paramref name="index"/> (0 = top), given the panel's center and height.</summary>
            public static double TileCenterY(double panelCenterY, double panelHeight, int index)
            {
                double top = panelCenterY + panelHeight / 2.0;
                return top - HeaderPad - index * TilePitch - TileHeight / 2.0;
            }

            /// <summary>Right-edge attachment X (a link leg leaves here and risers to its link-row Y).</summary>
            public static double RightEdgeX(double panelCenterX) => panelCenterX + Width / 2.0;

            /// <summary>Left-edge attachment X (the CAT6 tap from the HOME NETWORK trunk lands here).</summary>
            public static double LeftEdgeX(double panelCenterX) => panelCenterX - Width / 2.0;

            /// <summary>Top-edge Y (the 120 V feed stub rises from here).</summary>
            public static double TopEdgeY(double panelCenterY, double panelHeight) => panelCenterY + panelHeight / 2.0;
        }

        /// <summary>The module-tile family (ControlModuleDetail) — placed once per filled module slot, at the
        /// <see cref="Panel.TileCenterY"/> for its index. Brand-agnostic: a labeled cell reused across brands.</summary>
        public static class Module
        {
            public const string PartNumberParam = "PartNumber";   // e.g. "LQSE-4A-D"
        }

        /// <summary>The LV-compartment tile family (ControlLvSlotDetail) — the "Processor / Empty" box; placed
        /// once per LV slot (PD8/PD9 = 1, LV21 = 2), at the tile index after the modules. Its label is the
        /// processor part number, "EMPTY", or an IO/interface device. Brand-agnostic.</summary>
        public static class LvSlot
        {
            public const string LabelParam = "Label";
        }

        /// <summary>
        /// A shade panel (QSPS-10PNL) — same width as a power panel, shorter. BOTTOM-ALIGNED with the power
        /// panels on the QS link (origin on the shared baseline), so it takes the same caret tap at its origin;
        /// labeled with its part number + <c>n/10</c> fill. One motor leg rises out the TOP as a single
        /// <c>n MOTORS</c> stub (v1 — no shade symbols; the <see cref="MotorTap"/> anchor v2 fans per motor).
        /// </summary>
        public static class ShadePanel
        {
            public const double Width = (71.0 + 197.0 / 256.0) / 12.0;    // 5'-11 197/256" (measured; == power panel W)
            public const double Height = (93.0 + 103.0 / 256.0) / 12.0;   // 7'-9 103/256"  (measured; == LV21 H)

            // Label params the renderer writes on the ControlSmartPanelDetail family.
            public const string NameParam = "PanelName";
            public const string FillParam = "Fill";
            public const string PartNumberParam = "PartNumber";

            /// <summary>Motor-leg tap from the node CENTER — TOP mid; the n-MOTORS stub rises from here (v1) /
            /// fans per motor (v2). The QS link attaches at the bottom ORIGIN via the caret, not a side point.</summary>
            public static readonly XY MotorTap = new XY(0, Height / 2.0);
        }

        /// <summary>Wire-type marker — Generic Annotation placed ON a wire; <c>WireMark</c> = the per-job
        /// legend number (dense 1..N). Mirrors the DMX marker.</summary>
        public static class Marker
        {
            public const string NumberParam = "WireMark";
        }

        /// <summary>
        /// The generator's own arrangement choices (not family facts) — tunable cosmetics. The planner stacks
        /// bays and nodes using these; because panel heights vary, it SUMS heights + gaps (rather than a fixed
        /// pitch) the way the DMX planner summed feed blocks. All model feet.
        /// </summary>
        public static class Layout
        {
            /// <summary>Vertical spacing between link ROWS, panel-origin to panel-origin (== center-to-center,
            /// since every row's panels are the uniform-height power enclosure). Each processor's links are
            /// flattened into rows top-down; up to 4 rows fit a 42×30 sheet. Sized so 4 rows fill the usable
            /// height with clearance.</summary>
            public const double RowPitch = (28.0 * 12.0) / 12.0;   // 28'-0" origin-to-origin

            /// <summary>Horizontal edge-to-edge gap between consecutive power panels on a link (holds the wire +
            /// marker). Derived so panel CENTER-TO-CENTER is exactly the target below (c-c = <see cref="Panel.Width"/>
            /// + this gap); re-measuring the family width keeps the 10' c-c automatically. Tune by the c-c target.</summary>
            public const double NodeGap = PanelCenterToCenter - Panel.Width;   // ⇒ 4'-0 59/256" gap at the current width

            /// <summary>Target panel center-to-center spacing along a link: 10'-0".</summary>
            public const double PanelCenterToCenter = 120.0 / 12.0;   // 10'-0"

            /// <summary>Processor-column center X — the left rail; bays hang their panels here.</summary>
            public const double ProcessorColumnX = 0.0;

            // ── Head → column-1 link fan (the head's QS links exit its RIGHT edge, dogleg through this gap in
            //    separate lanes, and drop to each row's spine — Screenshot_593 / 582). KNOBS the user tunes:
            //    (1) the head↔column-1 gap; (2) the exit points on the head's right edge; (3) the lane (bend) X. ──
            /// <summary><b>KNOB 1.</b> Head-column center to column-1 center. Wider than
            /// <see cref="PanelCenterToCenter"/> (the inter-panel spacing) so the fan of separate QS link lanes
            /// fits in the gap between the head and the first panel column.</summary>
            public const double HeadColumnCenterToCenter = 180.0 / 12.0;   // 15'-0" (> the 10' inter-panel c-c)

            /// <summary>Edge-to-edge head→column-1 gap the link-lane fan nests into (derived from
            /// <see cref="HeadColumnCenterToCenter"/> so re-measuring the panel width keeps the c-c target).</summary>
            public const double HeadColumnGap = HeadColumnCenterToCenter - Panel.Width;   // ⇒ ~8'-0" gap

            /// <summary><b>KNOB 2.</b> Height of the DEEPEST (last) row's link exit above the head's ORIGIN
            /// (center-bottom of the artwork). Anchored to the bottom-origin — NOT the top edge — so exits stay low
            /// regardless of enclosure height (a tall PD8 vs a short LV21). Shallower rows stack UP from here at
            /// <see cref="HeadExitPitch"/> (deepest lowest, each earlier row one pitch higher — matches the lane
            /// nesting and stays crossing-free).</summary>
            public const double LastRowExitAboveOrigin = 9.0 / 12.0;   // 0'-9"
            /// <summary><b>KNOB 2.</b> Vertical spacing between stacked link exits on the head's right edge
            /// (each shallower row this far ABOVE its successor). A 4-link LV21's top exit lands at
            /// <see cref="LastRowExitAboveOrigin"/> + 3× this.</summary>
            public const double HeadExitPitch = 18.0 / 12.0;       // 1'-6"

            /// <summary><b>KNOB 3.</b> X of link lane <paramref name="laneFromLeft"/> of
            /// <paramref name="laneCount"/>, evenly distributed across the head→column-1 gap
            /// [<paramref name="gapStartX"/>, <paramref name="gapEndX"/>] (endpoints excluded). Lane 0 = leftmost =
            /// deepest row, so the doglegs never cross (a deeper link turns right below the shallower lanes' ends).</summary>
            public static double LaneX(double gapStartX, double gapEndX, int laneFromLeft, int laneCount)
                => gapStartX + (gapEndX - gapStartX) * (laneFromLeft + 1.0) / (laneCount + 1.0);

            // The CAT6 tap enters the head bottom-left IN-LINE with the bottom (deepest-row) link exit on the right
            // edge — it reuses <see cref="LastRowExitAboveOrigin"/> as its height so the two stay aligned by
            // construction (clear of the QS fan on the right and the 120 V feed on top; Screenshot_582).

            // ── HOME NETWORK node (shared LAN switch; a CAT6 leg taps to each processor's left edge) ──
            // Box matches the module-tile footprint so it reads as a sibling glyph on the sheet.
            public const double HomeNetworkWidth = 4.0 + (7.0 + 107.0 / 128.0) / 12.0;   // 4'-7 107/128" (== module tile W)
            public const double HomeNetworkHeight = Panel.TileHeight;                    // 1'-9 15/32" (== module tile H)

            /// <summary>HOME NETWORK box CENTER relative to the TOP head's ORIGIN (bottom-center of the artwork):
            /// <see cref="HomeNetworkOffsetX"/> left of the head column, <see cref="HomeNetworkOffsetY"/> up —
            /// level with the first module tile slot (rung 1) so the node reads in-line with the panels' bottom
            /// tile. The vertical CAT6 trunk drops straight from this X.</summary>
            public const double HomeNetworkOffsetX = -120.0 / 12.0;                    // 10'-0" left of the head column
            public static readonly double HomeNetworkOffsetY = Panel.RungOffsets[0];   // 3'-6 7/8" (first tile slot)

            /// <summary>120 V feed stub length (rises from a panel's top edge). Still used by the shade motor-tap
            /// stub; the 120 V feed itself now uses the L-run knobs below.</summary>
            public const double FeedStubLength = 12.0 / 12.0;     // 1'-0"

            // ── 120 V feed (Lutron-style L: up from the panel top, LEFT to a terminus SQUARE, "120V" above it).
            //    All renderer-drawn; the square is a plain glyph — NOT tied to a marker or the wire legend. ──
            public const double Feed120VLegDx = -18.0 / 12.0;            // vertical leg, 1'-6" left of the node center
            public const double Feed120VRise = 12.0 / 12.0;             // vertical leg height above the node top edge
            public const double Feed120VRun = 24.0 / 12.0;             // horizontal run LEFT to the terminus square
            public const double Feed120VSquare = 7.0 / 12.0;          // terminus square side (plain glyph)
            public const double Feed120VLabelDx = (2.0 + 1.0 / 2.0) / 12.0;          // "120V" X: 2 1/2" right of the square's right edge
            public const double Feed120VLabelAboveCorner = (7.0 + 3.0 / 4.0) / 12.0;  // "120V" insertion Y: 7 3/4" above the L corner

            // ── QS link spine + panel caret (child/orphan panels tap the daisy from below, Lutron-style) ──
            /// <summary>The QS daisy-chain (spine) runs this far BELOW a child panel's bottom origin; each panel
            /// taps up to it with a caret. Equals the caret height, so the caret feet land on the spine.</summary>
            public const double LinkSpineDropFt = 12.0 / 12.0;    // 1'-0"
            /// <summary>Caret (chevron) half-width — apex at the panel origin, each foot this far to the side
            /// (so a 2'-0"-wide caret). Its height is <see cref="LinkSpineDropFt"/> (apex − spine).</summary>
            public const double CaretHalfWidth = 12.0 / 12.0;     // 1'-0"  (2'-0" total width)

            // ── Keypad tail zone (v1 = one REFER-TO-PLAN stub; v2 expands into a compact column-wrapped list) ──
            /// <summary>Horizontal gap from the last node's right edge to the keypad-tail anchor.</summary>
            public const double KeypadTailGap = NodeGap;
            /// <summary>Half-height of the terminal tick drawn at the keypad-tail anchor.</summary>
            public const double KeypadTickHalf = 6.5 / 12.0;
            /// <summary>"ALL KEYPADS" (line 1) insertion Y above the spine.</summary>
            public const double KeypadLine1Dy = 7.0 / 12.0;              // 0'-7"
            /// <summary>"(MAX 10 KEYPADS PER HOMERUN)" (line 2) insertion Y above the spine.</summary>
            public const double KeypadLine2Dy = (1.0 / 4.0) / 12.0;      // 0'-0 1/4"
            /// <summary>"WIRELESS KEYPADS" insertion Y above the CC-A stub (2" base + 1.55" nudge).</summary>
            public const double WirelessLabelDy = 3.55 / 12.0;          // ≈ 0'-3.55"

            /// <summary>Boilerplate note block origin (top-left of the page content), model feet from page origin.</summary>
            public static readonly XY BoilerplateOrigin = XY.In(0, 0);
        }

        /// <summary>
        /// A cross-page continuation glyph (Lutron's "To Sheet N" / "See Sheet N" bubble). Placed where a QS
        /// link or the inter-processor CAT6 is cut by the page boundary; the matching bubble on the next page
        /// carries the same number so a reader can follow the wire across sheets.
        /// </summary>
        public static class ContinuationBubble
        {
            public const double Radius = 9.0 / 12.0;
            public const string SheetParam = "SheetRef";   // e.g. "2" → renders "TO SHEET 2"
        }

        /// <summary>
        /// The per-job wire-legend view's layout — a title over a vertical list of circled marker numbers +
        /// wire-type labels. Same view scale + text height as the pages so the circled numbers match the wire
        /// markers exactly. Mirrors <c>DmxOneLineGeometry.Legend</c>. The wire-type ROSTER lives in
        /// <c>ControlWireLegend</c> (to author next); this is only its geometry.
        /// </summary>
        public static class Legend
        {
            public const double MarkerX = 0.0;
            public const double LabelX = 4.5 / 12.0;
            public const double RowPitch = 7.0 / 12.0;
            public const double TitleGap = 12.0 / 12.0;
            public const string Title = "WIRE LEGEND";
            public const double TitleTextHeightFt = (3.0 / 32.0) / 12.0;
            public const double BorderOffset = 3.0 / 12.0;
        }
    }
}
