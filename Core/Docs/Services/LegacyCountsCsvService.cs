using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Raw-CSV "legacy" Counts export — a dramatically simplified stand-in for the ClosedXML
/// workbook during the transition to TurboSuite. Two columns: <c>Type Mark</c>, <c>Count</c>.
///
/// <para>
/// Reproduces the old native-Revit + Excel ritual: a schedule of Type Mark / Count / Linear Length,
/// itemize-off (so Count is per-Type and Linear Length is the summed total), Linear Length rendered as
/// feet-and-fractional-inches rounded to the nearest 1", then hand-concatenated onto the Type Mark with
/// the <c>'-</c>/<c>"</c> marks swapped for <c>ft</c>/<c>in</c> and zero-length rows left bare. A linear
/// Type's count is forced to <c>1</c> — the length token already carries the total run to purchase, so the
/// instance count is dropped to stop the reader multiplying length × instances:
/// <code>
///   TL, 1,  443'-4"  →  TL-443ft4in, 1
///   TF, 1,  32'-0"   →  TF-32ft0in,  1
///   FA, 14, 140'-4"  →  FA-140ft4in, 1   (not 14 — token is the whole buy)
///   A2, 24, (none)   →  A2,          24
/// </code>
/// Formatting only — no math, no Catalog Number logic. Consumes the same
/// <see cref="CountsFixtureModel"/> list the workbook export uses (already sorted by Type Mark), so
/// every collected Type appears, linear or not.
/// </para>
/// </summary>
public static class LegacyCountsCsvService
{
    /// <summary>
    /// Renders a summed Linear Length (feet) as the <c>{ft}ft{in}in</c> token appended to a Type Mark.
    /// Rounds to the nearest inch (half away from zero). Feet are always shown, including <c>0ft</c> for a
    /// sub-foot length (e.g. 8" → <c>0ft8in</c>). Returns empty for a zero (or zero-rounding) length —
    /// the caller then emits the bare Type Mark.
    /// </summary>
    public static string FormatLength(double feet)
    {
        int totalInches = (int)Math.Round(feet * 12.0, MidpointRounding.AwayFromZero);
        if (totalInches <= 0) return string.Empty;
        int ft = totalInches / 12;
        int inch = totalInches % 12;
        return $"{ft}ft{inch}in";
    }

    /// <summary>
    /// The Type Mark as it appears in the legacy CSV: bare for a non-linear Type, or
    /// <c>{TypeMark}-{ft}ft{in}in</c> when the summed Linear Length is positive.
    /// </summary>
    public static string FormatTypeMark(CountsFixtureModel fixture)
    {
        string length = FormatLength(fixture.LinearLength);
        return length.Length == 0 ? fixture.TypeMark : $"{fixture.TypeMark}-{length}";
    }

    /// <summary>
    /// The Count column value for the legacy CSV. For a linear Type the appended <c>{ft}ft{in}in</c> token
    /// already carries the summed run to purchase (e.g. <c>FA-140ft4in</c> means "buy enough reels for
    /// 140'-4""), so the count is forced to <c>1</c> — emitting the instance count instead would make the
    /// reader multiply length × instances (14 × 140'-4"). Non-linear Types keep their real instance count.
    /// Keyed off the same rendered length as <see cref="FormatTypeMark"/>, so a length that rounds away to a
    /// bare Type Mark also keeps its real count.
    /// </summary>
    public static int FormatCount(CountsFixtureModel fixture)
    {
        return FormatLength(fixture.LinearLength).Length == 0 ? fixture.Count : 1;
    }

    /// <summary>
    /// Builds the full CSV text: one row per Type, in the order supplied (the collector hands back its
    /// list already sorted by Type Mark). No header row — the first line is data. CRLF line endings,
    /// RFC-4180 quoting.
    /// </summary>
    public static string BuildCsv(IEnumerable<CountsFixtureModel> fixtures)
    {
        var sb = new StringBuilder();
        foreach (var f in fixtures)
        {
            sb.Append(Escape(FormatTypeMark(f)));
            sb.Append(',');
            sb.Append(FormatCount(f).ToString(CultureInfo.InvariantCulture));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // RFC-4180: quote a field only when it contains a comma, quote, CR, or LF; embedded quotes double.
    private static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0) return field;
        return $"\"{field.Replace("\"", "\"\"")}\"";
    }
}
