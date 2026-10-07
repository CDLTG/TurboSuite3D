using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.OneLine;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the shared located-list layout (Phase D; Core/Zones/OneLine/ControlListLayout.cs).
    //  Pure: a row count + geometry knobs → positioned rows. The firm rules: fill a column BOTTOM-UP,
    //  wrap to the next column on the RIGHT when full, and format a keypad row as "[Switch ID] · Room ·
    //  Type" dropping blank fields. Keypad tail and shade motor drop share this one renderer.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class ControlListLayoutTests
    {
        private static readonly XY Anchor = new XY(10.0, 5.0);

        [Fact]
        public void FillsBottomUp_Column0_FirstRowAtAnchor()
        {
            var rows = ControlListLayout.Layout(3, Anchor, rowsPerColumn: 10, rowPitch: 1.0, columnWidth: 4.0);

            // Row 0 at the anchor; each later row one pitch HIGHER (+Y), same column/X.
            Assert.Equal(10.0, rows[0].Anchor.X, 6);
            Assert.Equal(5.0, rows[0].Anchor.Y, 6);
            Assert.Equal(6.0, rows[1].Anchor.Y, 6);
            Assert.Equal(7.0, rows[2].Anchor.Y, 6);
            Assert.All(rows, r => Assert.Equal(0, r.Column));
        }

        [Fact]
        public void WrapsRight_WhenColumnFull()
        {
            // 12 rows, 10 per column → rows 0-9 in column 0, rows 10-11 in column 1.
            var rows = ControlListLayout.Layout(12, Anchor, rowsPerColumn: 10, rowPitch: 1.0, columnWidth: 4.0);

            Assert.Equal(0, rows[9].Column);
            Assert.Equal(9, rows[9].Slot);
            Assert.Equal(14.0, rows[9].Anchor.Y, 6);     // top of column 0 (anchor.Y + 9*pitch)

            Assert.Equal(1, rows[10].Column);
            Assert.Equal(0, rows[10].Slot);              // back to the BOTTOM of the next column
            Assert.Equal(14.0, rows[10].Anchor.X, 6);    // anchor.X + 1*columnWidth
            Assert.Equal(5.0, rows[10].Anchor.Y, 6);     // bottom again
        }

        [Fact]
        public void RowsPerColumn_ClampedToAtLeastOne()
        {
            var rows = ControlListLayout.Layout(3, Anchor, rowsPerColumn: 0, rowPitch: 1.0, columnWidth: 4.0);
            // Each row its own column, growing right.
            Assert.Equal(new[] { 0, 1, 2 }, rows.Select(r => r.Column).ToArray());
            Assert.Equal(18.0, rows[2].Anchor.X, 6);     // anchor.X + 2*columnWidth
        }

        [Fact]
        public void EmptyList_NoPositions()
        {
            Assert.Empty(ControlListLayout.Layout(0, Anchor, 10, 1.0, 4.0));
        }

        [Fact]
        public void HomerunBreaks_InsertAGapAfterEachHomerunWithinAColumn()
        {
            // 30 rows/column, homeruns of 10, gap 2.0, pitch 1.0. Within a column:
            //  slot 0-9  → homerun 0 (no gap), slot 10 → homerun 1 (+1 gap), slot 20 → homerun 2 (+2 gaps).
            var rows = ControlListLayout.Layout(25, Anchor, rowsPerColumn: 30, rowPitch: 1.0,
                columnWidth: 4.0, homerunSize: 10, homerunGap: 2.0);

            Assert.Equal(5.0, rows[0].Anchor.Y, 6);                 // anchor
            Assert.Equal(14.0, rows[9].Anchor.Y, 6);                // top of homerun 0: 5 + 9*1
            Assert.Equal(5.0 + 10 * 1.0 + 1 * 2.0, rows[10].Anchor.Y, 6);   // homerun 1 starts: +1 gap = 17
            Assert.Equal(5.0 + 20 * 1.0 + 2 * 2.0, rows[20].Anchor.Y, 6);   // homerun 2 starts: +2 gaps = 29
            Assert.All(rows, r => Assert.Equal(0, r.Column));       // 25 rows all fit one 30-row column
        }

        [Fact]
        public void HomerunSizeZero_NoBreaks_BackwardCompatible()
        {
            var withZero = ControlListLayout.Layout(12, Anchor, 30, 1.0, 4.0, homerunSize: 0, homerunGap: 2.0);
            var plain = ControlListLayout.Layout(12, Anchor, 30, 1.0, 4.0);
            Assert.Equal(plain.Select(p => p.Anchor.Y), withZero.Select(p => p.Anchor.Y));
        }

        // ── Row label formatting: "[Switch ID] · Room · Type", blanks dropped ─────────────────────

        // Universal "[number] Room - Description" row format (keypads and motors share it).

        [Fact]
        public void Label_AllThreeFields()
        {
            var r = new KeypadRecord("K1", "ENTRY", "Palladiom", location: 1);
            Assert.Equal("[K1] ENTRY - Palladiom", ControlListLayout.KeypadRowLabel(r));
        }

        [Fact]
        public void Label_DropsBlankSwitchId_NoLeadingSpace()
        {
            var r = new KeypadRecord("", "ENTRY", "Palladiom", location: 1);
            Assert.Equal("ENTRY - Palladiom", ControlListLayout.KeypadRowLabel(r));
        }

        [Fact]
        public void Label_DropsBlankRoomAndModel()
        {
            var r = new KeypadRecord("K7", "", "", location: 1);
            Assert.Equal("[K7]", ControlListLayout.KeypadRowLabel(r));
        }

        [Fact]
        public void Label_DropsBlankModel_NoDanglingDash()
        {
            var r = new KeypadRecord("K7", "GYM", "", location: 1);
            Assert.Equal("[K7] GYM", ControlListLayout.KeypadRowLabel(r));
        }

        // ── Motor row label: "[circuit #] <load name>" (load name already carries "Room - Desc") ──

        [Fact]
        public void MotorLabel_BracketedCircuitThenLoadName()
        {
            var r = new ShadeMotorRecord("M03", "SOUTH HALLWAY - shade motor 'a'");
            Assert.Equal("[M03] SOUTH HALLWAY - shade motor 'a'", ControlListLayout.MotorRowLabel(r));
        }

        [Fact]
        public void MotorLabel_DropsBlankLoadName()
        {
            Assert.Equal("[M03]", ControlListLayout.MotorRowLabel(new ShadeMotorRecord("M03", "")));
        }
    }
}
