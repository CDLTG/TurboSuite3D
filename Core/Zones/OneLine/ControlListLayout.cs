#nullable enable
using System.Collections.Generic;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>One positioned row of a located list — its order index, its column/slot, and the
    /// anchor point (bottom-left baseline) the planner draws the glyph + label from.</summary>
    public readonly struct ListRowPosition
    {
        public ListRowPosition(int index, int column, int slot, XY anchor)
        {
            Index = index;
            Column = column;
            Slot = slot;
            Anchor = anchor;
        }

        /// <summary>0-based row index in draw order (row 0 is the bottom of column 0).</summary>
        public int Index { get; }

        /// <summary>0-based column (0 = leftmost); a full column wraps to the next on the right.</summary>
        public int Column { get; }

        /// <summary>0-based slot within the column (0 = bottom; fills upward).</summary>
        public int Slot { get; }

        /// <summary>The row's anchor point (bottom-left baseline) in model feet.</summary>
        public XY Anchor { get; }
    }

    /// <summary>
    /// The shared column-wrapped list layout for the control one-line's located lists (Phase D) —
    /// keypads at the link tail and shade motors at the motor drop, one renderer, two anchors. A list
    /// fills a column <b>from the bottom up</b> (so it reads in the same visual language as the
    /// bottom-up DIN tiles beside it) and wraps to a new column on the <b>right</b> when a column is
    /// full, so it grows WIDE rather than tall past the row band — the whole point of columns is that
    /// everything stays on one page (no continuation bubbles).
    ///
    /// Pure and oracle-testable: a row count and the geometry knobs in →
    /// positions out. The planner pairs the positions with the records and draws glyph + label at each.
    /// </summary>
    public static class ControlListLayout
    {
        /// <param name="rowCount">How many rows (keypads / motors) to place.</param>
        /// <param name="anchor">The block's bottom-left — row 0 sits here; rows stack up (+Y), columns grow right (+X).</param>
        /// <param name="rowsPerColumn">Rows a column holds before wrapping right (= homeruns-per-column × homerunSize).
        /// Clamped to ≥ 1.</param>
        /// <param name="rowPitch">Vertical spacing between rows, model feet (+Y is up).</param>
        /// <param name="columnWidth">Horizontal spacing between columns, model feet.</param>
        /// <param name="homerunSize">Rows per homerun (the daisy-chain cap, ≤10). A visible
        /// <paramref name="homerunGap"/> is inserted after every homerun WITHIN a column, so several stacked
        /// homeruns still read as distinct chains. 0 ⇒ no breaks (one continuous column).</param>
        /// <param name="homerunGap">Extra vertical space between homerun blocks within a column, model feet.</param>
        public static IReadOnlyList<ListRowPosition> Layout(int rowCount, XY anchor,
            int rowsPerColumn, double rowPitch, double columnWidth,
            int homerunSize = 0, double homerunGap = 0.0)
        {
            if (rowsPerColumn < 1) rowsPerColumn = 1;
            var result = new List<ListRowPosition>(rowCount < 0 ? 0 : rowCount);
            for (int r = 0; r < rowCount; r++)
            {
                int column = r / rowsPerColumn;
                int slot = r % rowsPerColumn;
                // Homerun breaks: each full homerun below this slot pushes it up by one gap.
                int homerunsBelow = homerunSize > 0 ? slot / homerunSize : 0;
                double y = anchor.Y + slot * rowPitch + homerunsBelow * homerunGap;
                var pos = new XY(anchor.X + column * columnWidth, y);
                result.Add(new ListRowPosition(r, column, slot, pos));
            }
            return result;
        }

        /// <summary>A keypad row label in the universal <c>[number] Room - Description</c> form:
        /// <c>[Switch ID] Room - Model</c> (e.g. <c>[K1] ENTRY - Palladiom</c>). The Switch ID is bracketed
        /// (unnumbered keypads drop it), a space joins it to the room, and a dash joins the model. Any blank
        /// field is dropped, with the separators collapsing so there is never a dangling bracket or dash.</summary>
        public static string KeypadRowLabel(KeypadRecord record)
            => FormatRow(record.SwitchId, record.Room, record.Model);

        /// <summary>A motor row label in the universal <c>[number] Room - Description</c> form:
        /// <c>[shade circuit #] &lt;load name&gt;</c> (e.g. <c>[M03] SOUTH HALLWAY - shade motor 'a'</c>).
        /// The load name already carries the <c>Room - Description</c> part, so it joins the bracketed
        /// circuit with just a space.</summary>
        public static string MotorRowLabel(ShadeMotorRecord record)
            => FormatRow(record.Circuit, record.LoadName, description: null);

        /// <summary>The shared <c>[number] Room - Description</c> assembly: a bracketed number, a space to
        /// the room, then <c> - </c> to the description. Blank parts are dropped and the separators collapse,
        /// so there is never an empty bracket, leading space, or dangling dash.</summary>
        private static string FormatRow(string? number, string? room, string? description)
        {
            var head = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(number)) head.Add($"[{number!.Trim()}]");
            if (!string.IsNullOrWhiteSpace(room)) head.Add(room!.Trim());
            string headStr = string.Join(" ", head);

            if (string.IsNullOrWhiteSpace(description)) return headStr;
            return headStr.Length > 0 ? $"{headStr} - {description!.Trim()}" : description!.Trim();
        }
    }
}
