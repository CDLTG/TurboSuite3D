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
        /// Heuristic: does this text read as a ceiling-height annotation rather than a room name? True when it
        /// leads with a digit or <c>'+'</c> <b>and</b> carries a <c>'</c> or <c>"</c> mark. Requiring BOTH keeps
        /// a numeric-leading room name ("1-CAR GARAGE", "2ND FLOOR MECH" — digit-led but no foot/inch mark) out.
        /// Used only by the <c>sameLayer</c> path in the extractor, where heights share the room-name layer and
        /// must be split off so they don't seed a spurious region owner. Deliberately does NOT consult the
        /// descriptor keywords (VAULTED, …): that match is a loose substring test that trips on room names like
        /// "GRAND SITTING" (contains "TIN"), so a bare descriptor with no measurement stays classed as a name.
        /// </summary>
        public static bool LooksLikeHeight(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.TrimStart();
            bool leadsRight = char.IsDigit(t[0]) || t[0] == '+';
            bool hasMark = t.IndexOf('\'') >= 0 || t.IndexOf('"') >= 0;
            return leadsRight && hasMark;
        }

        /// <summary>
        /// Could this TextNote text have been produced as a ceiling <b>description</b> line by
        /// <see cref="Clean"/>? True iff every whitespace-separated token is letters-only AND contains one of
        /// the <see cref="PreservedWords"/> (case-insensitive).
        /// </summary>
        /// <remarks>
        /// This reproduces the exact output shape of <see cref="Clean"/>, which is the only producer of these
        /// notes: it joins the <c>[a-zA-Z]+</c> tokens that matched a keyword and upper-cases the result — so
        /// nothing with a digit, a slash, a period, or a non-keyword word can ever come out of it.
        ///
        /// TurboName's Clear &amp; Regenerate needs this because the description type (<c>AL_Annotation_3"</c>)
        /// is a general-purpose annotation type used for lots of other text — unlike the room-name type, its
        /// type id alone is NOT evidence that TurboName placed the note. Requiring EVERY token to qualify (not
        /// any) is what keeps "SLOPED CEILING" out: <i>CEILING</i> carries no keyword.
        ///
        /// Residual overlap: a hand-placed note whose entire content is a bare descriptor ("DROP", "TRAY")
        /// is indistinguishable from a generated one. Accepted — the clear reports its note count before
        /// deleting anything, and the whole clear+regenerate is one transaction, so Ctrl+Z restores it.
        /// </remarks>
        public static bool LooksLikeDescriptionNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var tokens = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            return tokens.All(t =>
                t.All(char.IsLetter) &&
                PreservedWords.Any(k => t.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
        }

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
