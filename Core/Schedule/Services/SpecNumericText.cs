#nullable disable
using System.Globalization;
using System.Text.RegularExpressions;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// The single source of truth for reconciling the workbook's numeric representation with the model's.
/// The collector's <c>ReadDisplay</c> (→ <c>SpecField.OriginalValue</c>) yields <b>unit-ful</b> display
/// strings (<c>AsValueString</c>, e.g. <c>"32 W"</c>, <c>"32.00 W"</c>, <c>"277 V"</c>), while the
/// workbook stores a <b>bare</b> value where it cleanly can (<c>"32"</c>). Both the export seed and both
/// sides of the Sync diff route through here so an untouched numeric cell never spuriously re-writes.
///
/// <para><b>Bare vs. verbatim.</b> <see cref="TryBare"/> succeeds only for a clean <i>scalar</i> display —
/// one leading number followed by a simple unit token (letters / <c>%</c> / <c>°</c>). It deliberately
/// fails for length and compound units — <c>3"</c>, <c>0' - 3"</c>, <c>1 1/2"</c>, <c>12 W/ft</c>,
/// <c>110 lm/W</c> — which fall back to verbatim (compared as strings). That fallback removes the whole
/// round-trip risk class for lengths/compounds; scalars get number-tolerant comparison via
/// <see cref="CompareKey"/>.</para>
///
/// <para>Only <c>ValueKind == Numeric</c> fields go through this helper; Text/Boolean have their own paths.
/// Write-back always uses <c>SetValueString</c>, which tolerates bare <i>or</i> unit-ful input — so this
/// helper governs comparison and seed aesthetics only, never the write itself.</para>
/// </summary>
public static class SpecNumericText
{
    // Leading signed number (optional thousands groups, optional decimals) + the trailing remainder.
    private static readonly Regex Leading = new(
        @"^\s*(?<num>-?\d[\d,]*(?:\.\d+)?)\s*(?<rest>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A "simple" unit token: letters, percent, or degree only. Empty is allowed (bare integer, no unit).
    // Anything with a slash (compound: W/ft, lm/W), a foot/inch mark, a digit, a space, or a hyphen fails.
    private static readonly Regex SimpleUnit = new(
        @"^[A-Za-z%°]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="display"/> is a clean scalar (leading number + simple unit), yielding the
    /// numeric token verbatim in <paramref name="bare"/>. Blank input is a trivial success (bare = ""). On
    /// failure <paramref name="bare"/> echoes the trimmed input, and the caller should treat the value as
    /// verbatim (length/compound).
    /// </summary>
    public static bool TryBare(string display, out string bare)
    {
        var s = (display ?? "").Trim();
        if (s.Length == 0) { bare = ""; return true; }

        var m = Leading.Match(s);
        if (m.Success && SimpleUnit.IsMatch(m.Groups["rest"].Value))
        {
            bare = m.Groups["num"].Value;
            return true;
        }

        bare = s;
        return false;
    }

    /// <summary>
    /// The canonical form used on <b>both</b> sides of the Sync diff. For a clean scalar whose numeric token
    /// parses as a double, the double formatted invariant with trailing zeros trimmed (so <c>"32"</c>,
    /// <c>"32.0"</c>, <c>"32.00 W"</c>, <c>"1,000"</c> all key equal to their magnitude). Otherwise the
    /// trimmed display verbatim (length/compound fields compare as exact strings).
    /// </summary>
    public static string CompareKey(string display)
    {
        if (TryBare(display, out var bare) && TryParseMagnitude(bare, out var d))
            return d.ToString("0.############", CultureInfo.InvariantCulture);

        return (display ?? "").Trim();
    }

    /// <summary>
    /// The value to seed into a freshly-appended numeric cell: the bare token when the model display is a
    /// clean scalar, else the verbatim display. (Write-back tolerates either form regardless.)
    /// </summary>
    public static string SeedCell(string display) =>
        TryBare(display, out var bare) ? bare : (display ?? "").Trim();

    private static bool TryParseMagnitude(string token, out double value)
    {
        // Thousands separators are display-only noise for comparison; strip before parsing invariant.
        var cleaned = (token ?? "").Replace(",", "");
        return double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
    }
}
