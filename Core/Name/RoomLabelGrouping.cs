#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Name.Regions
{
    /// <summary>A single CAD text entity on the room-name layer, normalized for grouping.</summary>
    /// <remarks>
    /// <see cref="Point"/> is the entity's raw insertion point in <b>CAD space, feet</b> — NOT Revit model
    /// space. Grouping runs pre-transform on purpose: the tests below key on the text's own X/Y axes, and a
    /// DWG inserted into Revit at a rotation would tilt those axes out from under them.
    /// <see cref="Text"/> is fully normalized (formatting stripped, '#' removed, upper-cased) because the
    /// horizontal test measures against its <b>length</b>.
    /// </remarks>
    public readonly struct LabelText
    {
        public LabelText(Pt point, string text, double height)
        {
            Point = point; Text = text ?? ""; Height = height;
        }

        /// <summary>Insertion point, CAD space, feet. For MTEXT this is the attachment corner (typically top-left).</summary>
        public Pt Point { get; }

        /// <summary>Normalized display text.</summary>
        public string Text { get; }

        /// <summary>Text (character) height in feet — the natural scale for every threshold here.</summary>
        public double Height { get; }
    }

    /// <summary>One room label: its joined text and the anchor point that represents it.</summary>
    public sealed record LabelCluster(Pt Anchor, string Text);

    /// <summary>
    /// Coalesces the separate CAD text entities that make up ONE multi-line room label into a single label.
    /// Revit-free; the Shim (<c>CadRoomExtractorService.ExtractTextMode</c>) calls this before turning room
    /// text into <c>CadRoomData</c>, so BOTH downstream consumers are fixed at the source: the watershed gets
    /// one seed per room instead of one per line (two seeds in one room split it down the middle), and the
    /// manual naming pass sees one name instead of tripping its ambiguous-region guard.
    ///
    /// WHY THIS IS NOT A PROXIMITY RADIUS. The obvious "merge labels within N feet" fails on real drawings.
    /// An MTEXT's insertion point is its attachment corner (top-left in practice), not its visual center, so
    /// the horizontal distance between two lines of one label is the JUSTIFICATION INDENT — which grows with
    /// how much shorter one line is than the other. A measured job puts "BAR/BREAKFAST" over "AREA" with
    /// insertion points 3.93 ft apart (dx 3.64, dy 1.49) while a genuinely unrelated pair sits closer. There
    /// is no radius that separates them.
    ///
    /// The insight is that the indent only pollutes X. With a top-anchored attachment the insertion point's Y
    /// is the TOP OF EACH LINE, so the vertical gap between two lines of one label is exactly the line spacing
    /// — independent of justification, string length, and glyph widths. So:
    ///   • VERTICAL  — dy must look like one line of spacing: [MinLineGap, MaxLineGap] × text height. Rejects
    ///     same-line neighbours (dy≈0) and different-room labels (dy = many line heights).
    ///   • HORIZONTAL — dx must be explicable as centring indent: half the width difference the two strings
    ///     could have, estimated as 0.5 × |len₁−len₂| × height, plus slack for equal-length lines whose glyph
    ///     widths still differ slightly. Rejects side-by-side rooms that happen to sit one line-height apart.
    /// Both gates cleared every case on the measured job with margin (see RoomLabelGroupingTests).
    ///
    /// Dead ends — DO NOT revisit (each was probed against a real DWG via TurboSpike before being dropped):
    ///  • MText.HorizontalWidth / VerticalHeight (DXF 42/43, the measured extents) to compute a visual center:
    ///    ACadSharp 3.6.35's DWG reader populates them with a CONSTANT 0.9 / 0.2 for every entity regardless
    ///    of content. Not the real extents. Unusable.
    ///  • MText.RectangleWidth as a width proxy: it is the reference/wrap rectangle, not a measured width —
    ///    it scattered (ratio to chars×height: mean 0.944, sd 0.207, min 0.037, max 1.484) and copy-pasted
    ///    entities all share one value. Unusable.
    ///  • TextEntity (DTEXT) has NO width property at all, so no width-based approach can ever cover it.
    ///  Conclusion: text width is not recoverable from ACadSharp. Everything here keys off dy + string length.
    ///
    /// KNOWN LIMITS (deliberate — each fails SAFE, i.e. back to today's no-merge behaviour):
    ///  • Rotated text is not handled. The dx/dy split assumes the label's axes are the DWG's axes. Room
    ///    labels are unrotated in practice; a rotated one simply won't merge.
    ///  • Non-top attachments (Middle*/Bottom*) shift Y by a constant per line, which the dy window absorbs
    ///    for uniform labels but not for mixed ones.
    /// </summary>
    public static class RoomLabelGrouping
    {
        // ── Tuned constants. All are MULTIPLES OF TEXT HEIGHT, never absolute feet, so they scale with the
        //    DWG's drawing scale for free. Validated against a measured job (see RoomLabelGroupingTests). ──

        /// <summary>Two lines of one label share a text height. Guards against merging a label with a
        /// different-sized annotation that happens to land one line-height away.</summary>
        private const double HeightMatchFrac = 0.25;

        /// <summary>Minimum dy, × height. Below this the two texts are on the same line — different rooms.
        /// (Lines cannot overlap, so any real label clears 1.0.)</summary>
        private const double MinLineGapFrac = 1.0;

        /// <summary>Maximum dy, × height. The DXF default "3-on-5" spacing puts one line at ~1.67 × height;
        /// this covers line-spacing factors from ~0.6 to ~1.5. Beyond it, two texts are separate labels.</summary>
        private const double MaxLineGapFrac = 2.5;

        /// <summary>Slack added to the indent bound, × height. Equal-length lines still differ by a few
        /// glyph widths ("COVERED" over "TERRACE" measured dx 0.12 ft at 0.896 ft height), which a pure
        /// length-difference bound would score as zero.</summary>
        private const double IndentSlackFrac = 0.5;

        /// <summary>
        /// Groups <paramref name="labels"/> into one <see cref="LabelCluster"/> per room label. Single
        /// (non-clustering) labels come back unchanged as one-entity clusters, so the caller can treat the
        /// result uniformly. Grouping is single-linkage, so a three-line label chains into one cluster.
        /// Output order is deterministic (first input index of each cluster).
        /// </summary>
        public static List<LabelCluster> Group(IReadOnlyList<LabelText> labels)
        {
            var result = new List<LabelCluster>();
            if (labels == null || labels.Count == 0) return result;

            int n = labels.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); // keep the lowest index as root
            }

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (IsSameLabel(labels[i], labels[j])) Union(i, j);

            // Bucket by root, preserving first-seen order so the output is stable.
            var order = new List<int>();
            var buckets = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!buckets.TryGetValue(r, out var list))
                {
                    buckets[r] = list = new List<int>();
                    order.Add(r);
                }
                list.Add(i);
            }

            foreach (int root in order)
            {
                var members = buckets[root]
                    .Select(i => labels[i])
                    // Reading order: top line first (a top-anchored insertion point means larger Y is higher),
                    // then left-to-right. "MASTER" over "BEDROOM" joins as "MASTER BEDROOM", not the reverse.
                    .OrderByDescending(l => l.Point.Y)
                    .ThenBy(l => l.Point.X)
                    .ToList();

                var parts = new List<string>();
                foreach (var m in members)
                {
                    string t = (m.Text ?? "").Trim();
                    if (t.Length == 0) continue;
                    // Collapse a genuinely duplicated entity ("BATH" stamped twice) rather than emit "BATH BATH".
                    if (!parts.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase)))
                        parts.Add(t);
                }

                // Anchor: the centroid of the member insertion points, which lands inside the label block the
                // architect drew. The watershed spirals to the nearest free pixel if it lands on a wall, and the
                // naming pass stamps its TextNote here.
                var anchor = new Pt(members.Average(m => m.Point.X), members.Average(m => m.Point.Y));
                result.Add(new LabelCluster(anchor, string.Join(" ", parts)));
            }

            return result;
        }

        /// <summary>True if two text entities are two lines of the SAME room label. See the type doc for why
        /// this is a dy window plus a length-derived dx bound rather than a distance.</summary>
        private static bool IsSameLabel(LabelText a, LabelText b)
        {
            if (a.Height <= 0 || b.Height <= 0) return false;

            double h = Math.Max(a.Height, b.Height);
            if (Math.Abs(a.Height - b.Height) > HeightMatchFrac * h) return false;

            // Vertical: one line of spacing apart.
            double dy = Math.Abs(a.Point.Y - b.Point.Y);
            if (dy < MinLineGapFrac * h || dy > MaxLineGapFrac * h) return false;

            // Horizontal: within what centring two strings of these lengths could indent.
            double dx = Math.Abs(a.Point.X - b.Point.X);
            double indentBound = 0.5 * Math.Abs(a.Text.Length - b.Text.Length) * h + IndentSlackFrac * h;
            return dx <= indentBound;
        }
    }
}
