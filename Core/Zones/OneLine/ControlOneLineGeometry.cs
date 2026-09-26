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

            public const double Width = 42.0 / 12.0;    // 3'-6"  (≈0.875" on paper) — holds a part number tile

            // Tile stack metrics (renderer draws each tile as a detail rectangle + centered part-number text).
            public const double TileHeight = 9.0 / 12.0;    // 0'-9"
            public const double TileGap = 1.5 / 12.0;       // gap between tiles
            public const double TileInset = 3.0 / 12.0;     // side inset of a tile inside the panel
            public const double HeaderPad = 3.0 / 12.0;     // top pad above the first tile
            public const double FooterHeight = 15.0 / 12.0; // name + fill + part number

            // Footer label params the renderer writes on the enclosure family (ControlPanelDetail /
            // ControlLv21Detail); the family author names its text params to match. Tiles are SEPARATE family
            // placements — see the sibling <see cref="Module"/> / <see cref="LvSlot"/> classes.
            public const string NameParam = "PanelName";
            public const string FillParam = "Fill";
            public const string PartNumberParam = "PartNumber";

            public static double TileWidth => Width - 2 * TileInset;
            public static double TilePitch => TileHeight + TileGap;

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
        /// A shade panel (QSPS-10PNL) — a SMALLER external enclosure than a power panel. Labeled with its part
        /// number + <c>n/10</c> fill; one motor leg drops from the bottom (v1 single stub — the
        /// <see cref="Layout.MotorDropZone"/> anchor that v2 expands into a per-motor fan).
        /// </summary>
        public static class ShadePanel
        {
            public const double Width = 30.0 / 12.0;    // 2'-6"
            public const double Height = 21.0 / 12.0;   // 1'-9"

            // Label params the renderer writes on the ControlSmartPanelDetail family.
            public const string NameParam = "PanelName";
            public const string FillParam = "Fill";
            public const string PartNumberParam = "PartNumber";

            public static readonly XY LinkIn = XY.In(-15, 0);    // left edge mid ← QS link
            public static readonly XY MotorDrop = XY.In(0, -10.5); // bottom mid → n-MOTORS leg (v1) / fan (v2)
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
            /// <summary>Vertical gap between one processor's two link rows (edge-to-edge between their panels).</summary>
            public const double LinkRowGap = 12.0 / 12.0;   // 1'-0"

            /// <summary>Vertical gap between consecutive processor bays.</summary>
            public const double BayGap = 24.0 / 12.0;       // 2'-0"

            /// <summary>Horizontal edge-to-edge gap between consecutive nodes on a link (holds the wire + marker).</summary>
            public const double NodeGap = 30.0 / 12.0;      // 2'-6"

            /// <summary>Processor-column center X — the left rail; bays hang their panels here.</summary>
            public const double ProcessorColumnX = 0.0;

            // ── HOME NETWORK node (shared LAN switch; a CAT6 leg taps to each processor's left edge) ──
            public const double HomeNetworkWidth = 30.0 / 12.0;   // 2'-6"
            public const double HomeNetworkHeight = 12.0 / 12.0;  // 1'-0"

            /// <summary>X of the vertical CAT6 trunk the HOME NETWORK drops; each bay taps it. Left of the
            /// panel's left edge (−<see cref="Panel.Width"/>/2) so the tap runs cleanly into the panel.</summary>
            public const double Cat6TrunkX = -36.0 / 12.0;        // 3'-0" left of the processor column

            /// <summary>120 V feed stub length (rises from a panel's top edge).</summary>
            public const double FeedStubLength = 12.0 / 12.0;     // 1'-0"

            // ── Keypad tail zone (v1 = one REFER-TO-PLAN stub; v2 expands into a compact column-wrapped list) ──
            /// <summary>Horizontal gap from the last node's right edge to the keypad-tail anchor.</summary>
            public const double KeypadTailGap = NodeGap;
            /// <summary>Half-height of the terminal tick drawn at the keypad-tail anchor.</summary>
            public const double KeypadTickHalf = 6.5 / 12.0;

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
