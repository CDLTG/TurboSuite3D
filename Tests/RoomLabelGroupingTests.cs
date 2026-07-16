using System.Collections.Generic;
using System.Linq;
using TurboSuite.Name.Regions;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for multi-line room-label coalescing (Core/Name/RoomLabelGrouping.cs).
    ///
    /// The coordinates below are NOT invented — they are the measured insertion points from a real job's
    /// A-AREA-IDEN layer, captured via TurboSpike against the linked DWG. Text height there is 10.75 CAD
    /// inches = 0.896 ft, and every genuine multi-line label sits dy = 1.49 ft apart (1.66 × height, the DXF
    /// "3-on-5" default line spacing). Keeping the real numbers means these tests pin the ACTUAL failure —
    /// "BAR/BREAKFAST" over "AREA" with a 3.64 ft justification indent — rather than a tidied-up model of it.
    /// </summary>
    public class RoomLabelGroupingTests
    {
        private const double H = 0.896; // text height, ft (10.75" at 12 px/ft)

        private static LabelText L(double x, double y, string text, double height = H)
            => new LabelText(new Pt(x, y), text, height);

        private static string TextAt(List<LabelCluster> cs, string startsWith)
            => cs.Single(c => c.Text.StartsWith(startsWith)).Text;

        // ── The three real multi-line labels: MERGE ──

        [Fact]
        public void Merges_BarBreakfastArea_DespiteLargeJustificationIndent()
        {
            // The case that motivated all of this: dIns 3.93 ft, dx 3.64 (indent), dy 1.49 (one line).
            // A naive 2 ft proximity radius misses this entirely.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(10.761, -8.878, "BAR/BREAKFAST"),
                L(14.400, -10.364, "AREA"),
            });

            var c = Assert.Single(clusters);
            Assert.Equal("BAR/BREAKFAST AREA", c.Text);
        }

        [Fact]
        public void Merges_CoveredTerrace_EqualLengthLinesWithNearZeroIndent()
        {
            // Equal-length lines: dx 0.12 ft comes purely from glyph-width differences, which the
            // length-difference bound scores as zero — this is what IndentSlackFrac exists for.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(43.316, -3.210, "COVERED"),
                L(43.441, -4.696, "TERRACE"),
            });

            Assert.Equal("COVERED TERRACE", Assert.Single(clusters).Text);
        }

        [Fact]
        public void Merges_Closet3_ShortSecondLine()
        {
            // In the DWG the "3" carries an MTEXT font code; this asserts the post-strip state (see the
            // StripCadFormatting \f fix). If stripping regresses, Text.Length is 25 instead of 1 and the
            // indent bound silently inflates — hence the length dependency is worth pinning.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(-0.849, -12.432, "CLOSET"),
                L(1.276, -13.919, "3"),
            });

            Assert.Equal("CLOSET 3", Assert.Single(clusters).Text);
        }

        // ── Near-misses that must NOT merge ──

        [Fact]
        public void DoesNotMerge_ShwrAndMech1_OneLineApartButHorizontallyDistant()
        {
            // The sharpest false-positive candidate in the job: dy 0.99 ft passes the vertical window
            // (1.11 × height), so ONLY the indent bound rejects it — dx 7.39 ft against a bound of 1.34.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(-12.086, -25.043, "SHWR"),
                L(-19.474, -26.034, "MECH 1"),
            });

            Assert.Equal(2, clusters.Count);
        }

        [Fact]
        public void DoesNotMerge_WcAndCloset_SameLine()
        {
            // dy 0.03 ft — same line, two different rooms. Rejected on the vertical window.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(-10.401, -12.398, "WC"),
                L(-0.849, -12.432, "CLOSET"),
            });

            Assert.Equal(2, clusters.Count);
        }

        [Fact]
        public void DoesNotMerge_LabelsManyLineHeightsApart()
        {
            // BAR/BREAKFAST → BEDROOM 3: dy 9.82 ft (10.96 × height). Different rooms.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(10.761, -8.878, "BAR/BREAKFAST"),
                L(8.741, -18.695, "BEDROOM 3"),
            });

            Assert.Equal(2, clusters.Count);
        }

        // ── Whole-floor behaviour ──

        [Fact]
        public void WholeFloor_CollapsesExactlyTheThreeMultiLineLabels()
        {
            // ALL 22 A-AREA-IDEN entities from the measured floor plan, verbatim. Exactly three pairs are
            // real multi-line labels, so this must yield 19 clusters — no more, no less. The regression guard
            // against a threshold change quietly over- or under-merging a whole floor.
            //
            // Ten OTHER pairs in this set fall inside the vertical window and are rejected only by the indent
            // bound — LIVING ROOM and FITNESS sit exactly one line spacing apart (dy 1.49) but 27 ft across
            // the floor. That is why the horizontal gate is not optional.
            var floor = new List<LabelText>
            {
                L(-2.126, 20.563, "MECH 2"),
                L(33.423, 14.501, "CLO. 4"),
                L(26.217, 13.495, "SHWR"),
                L(42.000, 10.391, "BEDROOM 4"),
                L(31.261, 10.381, "BATH 4"),
                L(17.224, 1.211, "LIVING ROOM"),
                L(-10.204, -0.276, "FITNESS"),
                L(46.056, -4.717, "COVERED"),
                L(9.523, -4.978, "BAR/BREAKFAST"),
                L(46.181, -6.203, "TERRACE"),      // + COVERED
                L(13.162, -6.464, "AREA"),         // + BAR/BREAKFAST
                L(-12.412, -12.406, "WC"),
                L(-0.956, -12.698, "CLOSET"),
                L(1.169, -14.184, "3"),            // + CLOSET; "3" only after the StripCadFormatting \f fix
                L(21.112, -14.984, "ELEV"),
                L(-19.610, -16.987, "MECH 1"),
                L(64.820, -17.060, "WATER FEATURE"),
                L(-6.381, -19.056, "BATH 3"),
                L(8.319, -19.620, "BEDROOM 3"),
                L(-10.780, -24.758, "SHWR"),
                L(20.432, -26.359, "PWDR"),
                L(42.792, -33.386, "OPEN TO ABOVE"),
            };

            var clusters = RoomLabelGrouping.Group(floor);

            Assert.Equal(19, clusters.Count);
            Assert.Equal("COVERED TERRACE", TextAt(clusters, "COVERED"));
            Assert.Equal("BAR/BREAKFAST AREA", TextAt(clusters, "BAR/BREAKFAST"));
            Assert.Equal("CLOSET 3", TextAt(clusters, "CLOSET"));
            // The near-miss rooms that must survive intact.
            Assert.Contains(clusters, c => c.Text == "LIVING ROOM");
            Assert.Contains(clusters, c => c.Text == "FITNESS");
            Assert.Contains(clusters, c => c.Text == "WC");
            Assert.Contains(clusters, c => c.Text == "MECH 1");
            Assert.Equal(2, clusters.Count(c => c.Text == "SHWR")); // two distinct SHWR rooms on this floor
        }

        // ── Shape of the output ──

        [Fact]
        public void Anchor_IsCentroidOfMembers()
        {
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(10.761, -8.878, "BAR/BREAKFAST"),
                L(14.400, -10.364, "AREA"),
            });

            var c = Assert.Single(clusters);
            Assert.Equal((10.761 + 14.400) / 2, c.Anchor.X, 3);
            Assert.Equal((-8.878 + -10.364) / 2, c.Anchor.Y, 3);
        }

        [Fact]
        public void SingleLabel_PassesThroughUnchanged()
        {
            var clusters = RoomLabelGrouping.Group(new List<LabelText> { L(5, 5, "PANTRY") });

            var c = Assert.Single(clusters);
            Assert.Equal("PANTRY", c.Text);
            Assert.Equal(5, c.Anchor.X, 3);
            Assert.Equal(5, c.Anchor.Y, 3);
        }

        [Fact]
        public void JoinsThreeLineLabel_InReadingOrder()
        {
            // Single-linkage chains line 1 → 2 → 3 even though lines 1 and 3 are two line-heights apart.
            // Deliberately supplied bottom-up to prove the ordering is geometric, not input order.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(0.0, -2.98, "SUITE"),
                L(0.0, -1.49, "BEDROOM"),
                L(0.0, 0.0, "MASTER"),
            });

            Assert.Equal("MASTER BEDROOM SUITE", Assert.Single(clusters).Text);
        }

        [Fact]
        public void CollapsesDuplicateText_RatherThanRepeatingIt()
        {
            // A doubled-up entity at one line spacing should not yield "BATH BATH".
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(0.0, 0.0, "BATH"),
                L(0.1, -1.49, "BATH"),
            });

            Assert.Equal("BATH", Assert.Single(clusters).Text);
        }

        [Fact]
        public void DoesNotMerge_DifferentTextHeights()
        {
            // A room label and a much smaller annotation one line-height away are not one label.
            var clusters = RoomLabelGrouping.Group(new List<LabelText>
            {
                L(0.0, 0.0, "GALLERY"),
                L(0.1, -1.49, "SEE PLAN", height: H * 0.5),
            });

            Assert.Equal(2, clusters.Count);
        }

        [Fact]
        public void EmptyInput_YieldsNoClusters()
        {
            Assert.Empty(RoomLabelGrouping.Group(new List<LabelText>()));
        }
    }
}
