#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TurboSuite.Name.Regions
{
    /// <summary>
    /// Normalizes a raw CAD ceiling-height annotation into the string that gets stamped as a TextNote,
    /// splitting off any descriptive keyword (VAULTED, SLOPE, TRAY, …) as a separate line.
    /// </summary>
    /// <remarks>
    /// The numeric height is always <b>parsed and reformatted</b> — feet, whole inches, and an <c>n/d</c>
    /// fraction are summed to total inches, <b>rounded to the nearest inch (half up)</b>, then split back to
    /// feet+inches with a <b>foot carry</b> (11½" → 12" → +1'). So <c>10'-6 1/2"</c> stamps as <c>10'-7"</c>
    /// and the internal fraction space never reaches the output — this is why there is no whitespace to
    /// preserve. A value with no <c>'</c>/<c>"</c> marks is not a height: its numeric part is dropped and only
    /// the description (if any) survives.
    /// </remarks>
    public static class CeilingHeightFormatter
    {
        // Ceiling shape/descriptor words kept as a separate description line (case-insensitive substring match).
        private static readonly string[] PreservedWords =
        {
            "Vault", "Slope", "Barrel", "Tray", "Tin",
            "Suspend", "Drop", "Cathedral", "Coffer", "Dome", "Groin", "Varie"
        };

        /// <summary>
        /// Returns the rounded, reformatted numeric height (e.g. <c>10'-7"</c>, or <c>""</c> when the value
        /// carries no foot/inch measurement) and any preserved descriptor keywords, upper-cased.
        /// </summary>
        public static (string Height, string Description) Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return (value, "");

            // Strip a leading '+' (e.g. "+10'-0\"").
            value = value.TrimStart('+');

            // Pull descriptor words (VAULTED, SLOPE, …) for the separate description line.
            var words = Regex.Matches(value, @"[a-zA-Z]+")
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => PreservedWords.Any(k =>
                    w.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            string description = words.Count > 0 ? string.Join(" ", words).ToUpperInvariant() : "";

            string height = FormatHeight(value);
            return (height, description);
        }

        /// <summary>
        /// Parses feet/whole-inches/fraction out of <paramref name="value"/>, rounds to the nearest inch
        /// (half up) with a foot carry, and reformats as <c>ft'-in"</c>. Returns <c>""</c> when the value has
        /// no <c>'</c> or <c>"</c> mark — a value with no measurement is not stampable as a height.
        /// </summary>
        private static string FormatHeight(string value)
        {
            bool hasFootMark = value.IndexOf('\'') >= 0;
            bool hasInchMark = value.IndexOf('"') >= 0;
            if (!hasFootMark && !hasInchMark) return "";

            double totalInches = 0;

            // Feet: the number (optionally decimal) immediately before a foot mark.
            var feetMatch = Regex.Match(value, @"(\d+(?:\.\d+)?)\s*'");
            if (feetMatch.Success)
                totalInches += ParseDouble(feetMatch.Groups[1].Value) * 12.0;

            // Inch region: text after the foot mark up to (and excluding) the inch mark; if there is no foot
            // mark, everything before the inch mark.
            string inchText = ExtractInchRegion(value, feetMatch.Success);

            // Fraction first (so its digits aren't misread as a whole-inch value), then the whole inches.
            var fracMatch = Regex.Match(inchText, @"(\d+)\s*/\s*(\d+)");
            if (fracMatch.Success)
            {
                double denom = ParseDouble(fracMatch.Groups[2].Value);
                if (denom != 0) totalInches += ParseDouble(fracMatch.Groups[1].Value) / denom;
                inchText = inchText.Remove(fracMatch.Index, fracMatch.Length);
            }
            var wholeMatch = Regex.Match(inchText, @"\d+(?:\.\d+)?");
            if (wholeMatch.Success)
                totalInches += ParseDouble(wholeMatch.Value);

            int rounded = (int)Math.Round(totalInches, MidpointRounding.AwayFromZero);
            int feet = rounded / 12;
            int inches = rounded % 12;
            return $"{feet}'-{inches}\"";
        }

        private static string ExtractInchRegion(string value, bool hadFoot)
        {
            int quote = value.IndexOf('"');
            if (quote < 0) return ""; // feet-only, e.g. "10'"
            int apos = value.IndexOf('\'');
            int start = (hadFoot && apos >= 0) ? apos + 1 : 0;
            if (start > quote) start = 0;
            return value.Substring(start, quote - start);
        }

        private static double ParseDouble(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
