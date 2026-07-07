#nullable enable
namespace TurboSuite.Dmx.OneLine
{
    /// <summary>A 2D point/offset in <b>model feet</b> (Revit internal units). <see cref="In"/> builds one
    /// from inches so the spec below reads in the units the families were authored in.</summary>
    public readonly struct XY
    {
        public XY(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }

        /// <summary>From inches → feet (e.g. <c>XY.In(-11, 0)</c> = 11" left of origin).</summary>
        public static XY In(double xInches, double yInches) => new XY(xInches / 12.0, yInches / 12.0);

        public XY Plus(XY o) => new XY(X + o.X, Y + o.Y);
        public XY Offset(double dx, double dy) => new XY(X + dx, Y + dy);
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>
    /// The authored one-line symbol library's geometry — the SOURCE OF TRUTH for the
    /// firm's authored families: family/type names, the instance label parameter
    /// names the renderer writes, box sizes, and the connection-point offsets (from each box's <b>center</b>
    /// origin) the line-drawing targets. All lengths are <b>model feet</b>; the families are Detail Items
    /// drawn at architectural size and read at the pinned view scale (1/4" = 1'-0"). The <see cref="Layout"/>
    /// block holds TurboDMX's own spacing choices (not family facts) — those are tunable cosmetics.
    /// Pure data: the Core planner and the shim renderer both read it so they can't disagree.
    /// </summary>
    public static class DmxOneLineGeometry
    {
        /// <summary>Pinned drafting-view scale: 1/4" = 1'-0" ⇒ ratio 1:48. The renderer sets this on the view.</summary>
        public const int ViewScale = 48;

        /// <summary>Paper text height for the generator's native notes (1/16"), expressed in feet.</summary>
        public const double NoteTextHeightFt = (1.0 / 16.0) / 12.0;

        /// <summary>Decoder box — DEC # + DMX address; power in (L), DMX daisy in (top) / out (bottom), single homerun (R).</summary>
        public static class Decoder
        {
            public const string Family = "AL_Detail_Decoder";
            public const string Type = "AL_Detail_Decoder";
            public const string DecNumberParam = "DecNumber";   // instance, Text — generator writes "DEC 20"
            public const string AddressParam = "Address";       // instance, Text — generator writes "001" (label adds [ ])

            public const double Width = 22.0 / 12.0;   // 1'-10"
            public const double Height = 7.0 / 12.0;   // 0'-7"

            public static readonly XY PowerIn = XY.In(-11, 0);     // left edge mid ← driver
            public static readonly XY DmxIn = XY.In(4.5, 3.5);     // top, +4.5" of center ← prev decoder / interface
            public static readonly XY DmxOut = XY.In(4.5, -3.5);   // bottom, +4.5" → next decoder / terminator
            public static readonly XY HomerunOut = XY.In(11, 0);   // right edge mid → single "REFER TO PLAN" leg
        }

        /// <summary>Driver box — Type Mark; power in (L) from the feed, out (R) to the decoder, 120 V daisy up/down.</summary>
        public static class Driver
        {
            public const string Family = "AL_Detail_Driver";
            public const string Type = "AL_Detail_Driver";
            public const string TypeMarkParam = "TypeMark";   // instance, Text — generator writes "CV"/"MD"/"ME"

            public const double Width = 16.0 / 12.0;   // 1'-4"
            public const double Height = 7.0 / 12.0;   // 0'-7"

            public static readonly XY PowerIn = XY.In(-8, 0);     // left edge mid ← 120V feed (first driver only)
            public static readonly XY PowerOut = XY.In(8, 0);     // right edge mid → decoder
            public static readonly XY DaisyDown = XY.In(0, -3.5); // bottom mid → next driver below
            public static readonly XY DaisyUp = XY.In(0, 3.5);    // top mid ← driver above (mirror of DaisyDown)
        }

        /// <summary>DMX Interface box — interface #; DMX chain out (bottom), comm in (R) from the processor.</summary>
        public static class Interface
        {
            public const string Family = "AL_Detail_DMX Interface";
            public const string Type = "AL_Detail_DMX Interface";
            public const string NumberParam = "DMXInterface";   // instance, Text — generator writes the interface #

            public const double Width = 24.0 / 12.0;   // 2'-0"
            public const double Height = 15.0 / 12.0;  // 1'-3"

            public static readonly XY ChainOut = XY.In(0, -7.5);   // bottom mid → first decoder's DMX in
            public static readonly XY CommIn = XY.In(-12, 0);      // left edge mid ↔ processor comm (processor sits left)
        }

        /// <summary>Lutron Processor box — static text; comm point (L) to the interface.</summary>
        public static class Processor
        {
            public const string Family = "AL_Detail_Processor";
            public const string Type = "AL_Detail_Processor";

            public const double Width = 24.0 / 12.0;   // 2'-0" (authored box)
            public const double Height = 15.0 / 12.0;  // 1'-3"

            public static readonly XY Comm = XY.In(12, 0);   // right edge mid ↔ interface comm (processor sits left)
        }

        /// <summary>DMX Terminator box — static text; DMX in (top) from the last decoder.</summary>
        public static class Terminator
        {
            public const string Family = "AL_Detail_Terminator";
            public const string Type = "AL_Detail_Terminator";

            public const double Width = 26.0 / 12.0;   // 2'-2"
            public const double Height = 15.0 / 12.0;  // 1'-3"

            public static readonly XY DmxIn = XY.In(0, 7.5);   // top mid ← last decoder's DMX out
        }

        /// <summary>Wire-type marker — Generic Annotation placed ON a wire; <c>WireMark</c> = the per-job
        /// legend # (dense, 1..N — the fixed 1–7 range is gone now that #16-N is uncapped).</summary>
        public static class Marker
        {
            public const string Family = "AL_Annotation_Wire Mark";
            public const string Type = "AL_Annotation_Wire Mark";
            public const string NumberParam = "WireMark";   // instance, Text — generator writes the legend #
        }

        /// <summary>
        /// TurboDMX's layout spacing — <b>not</b> family facts but the generator's own arrangement choices
        /// (the reference one-line style), tunable without touching a family. All model feet.
        /// </summary>
        public static class Layout
        {
            /// <summary>Vertical center-to-center between stacked decoder rows (7" box + 9" gap so the address
            /// labels + chain markers don't crowd the box above).</summary>
            public const double RowPitch = 16.0 / 12.0;          // 1'-4"

            /// <summary>Edge-to-edge gap between a driver's right and its decoder's left (the ③ leg + marker).</summary>
            public const double DriverDecoderGap = 10.0 / 12.0;   // 0'-10"

            /// <summary>Driver column center X (the per-loop origin's left rail). Decoder column derives from it.</summary>
            public const double DriverCenterX = 0.0;

            /// <summary>Decoder column center X = driver right edge + gap + decoder half-width.</summary>
            public const double DecoderCenterX =
                DriverCenterX + Driver.Width / 2.0 + DriverDecoderGap + Decoder.Width / 2.0;

            /// <summary>Vertical gap between consecutive 120 V FEED blocks (an extra row of breathing room). Held
            /// so RowPitch + FeedGroupGap stays 2'-0" ⇒ the feed-boundary decoder gap stays 1'-5".</summary>
            public const double FeedGroupGap = 8.0 / 12.0;       // 0'-8"

            /// <summary>Drop from the interface box center down to the first decoder row center.</summary>
            public const double InterfaceDrop = 24.0 / 12.0;     // 2'-0"

            /// <summary>Drop from the last decoder row center to the terminator box center (10" box-to-box gap).</summary>
            public const double TerminatorDrop = 21.0 / 12.0;    // 1'-9"

            /// <summary>Horizontal length of the single homerun leg out of each decoder (to the "REFER TO PLAN" note).</summary>
            public const double HomerunLegLength = 18.0 / 12.0;  // 1'-6"

            /// <summary>Horizontal length of the "120V FEED" stub into the first driver of a feed.</summary>
            public const double FeedStubLength = 18.0 / 12.0;    // 1'-6"

            /// <summary>Horizontal clearance from the homerun leg end to the designer-only sanity readout,
            /// parked far enough right to clear the "REFER TO PLAN" header so a sheet crop excludes it.</summary>
            public const double SanityBlockGap = 114.0 / 12.0;   // 9'-6"
        }

        /// <summary>
        /// The per-job wire-legend view's own layout — a title over a vertical list of
        /// rows, each a circled <see cref="Marker"/> number + the wire-type label. Model feet; same view
        /// scale + text height as the one-line so the circled numbers match the wire markers exactly.
        /// </summary>
        public static class Legend
        {
            /// <summary>Circled-number column center X (the list origin).</summary>
            public const double MarkerX = 0.0;

            /// <summary>Label text left X — clear of the circle.</summary>
            public const double LabelX = 4.5 / 12.0;    // 0'-4 1/2"

            /// <summary>Center-to-center between legend rows.</summary>
            public const double RowPitch = 7.0 / 12.0;  // 0'-7"

            /// <summary>Drop from the "WIRE LEGEND" title down to the first row.</summary>
            public const double TitleGap = 12.0 / 12.0; // 1'-0"

            /// <summary>Upward nudge applied to a row label's insertion Y so its glyph midline lands on the
            /// circled marker's center. A <c>TextNote</c> is anchored at its top edge (no vertical-alignment
            /// API), while the marker family is center-anchored — so raise the label by half the text cap
            /// height. <see cref="NoteTextHeightFt"/> is the <i>paper</i> size, so scale it to model space
            /// (these row positions are model feet) by <see cref="ViewScale"/>. The extra term is a measured
            /// correction for the line-box leading above the glyph cap (the insertion point sits above the
            /// visible top), tuned in-Revit so the row reads dead-on.</summary>
            public const double LabelMidlineNudge = NoteTextHeightFt * ViewScale / 2.0 + 0.95 / 12.0;

            public const string Title = "WIRE LEGEND";

            /// <summary>Title text height (paper). Larger than the 1/16" rows — the first 3/32" type is used.</summary>
            public const double TitleTextHeightFt = (3.0 / 32.0) / 12.0;

            /// <summary>Outward gap from the row/title extents to the enclosing border rectangle (model feet).</summary>
            public const double BorderOffset = 3.0 / 12.0;   // 0'-3"

            /// <summary>Downward trim on the border's top edge only — a TextNote's view bounding box carries
            /// extra headroom above the glyph cap (font + line-box leading), so the raw top extent sits too
            /// high. Pull the top line down to hug the title. Tuned in-Revit for this font.</summary>
            public const double BorderTopTrim = 1.25 / 12.0;   // 0'-1 1/4"
        }
    }
}
