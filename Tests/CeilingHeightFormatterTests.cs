using TurboSuite.Name.Regions;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for ceiling-height normalization (Core/Name/CeilingHeightFormatter.cs): parse feet /
    /// whole inches / fraction, round to the nearest inch (half up) with a foot carry, split off descriptors.
    /// </summary>
    public class CeilingHeightFormatterTests
    {
        [Theory]
        // Plain, already-clean values are unchanged.
        [InlineData("10'-0\"", "10'-0\"")]
        // Fractional inch rounds to the nearest inch (the TurboName-7 bug: was mangled to "10'-61/2\"").
        [InlineData("10'-6 1/2\"", "10'-7\"")]   // half rounds up
        [InlineData("10'-6 1/4\"", "10'-6\"")]   // rounds down
        [InlineData("10'-6 3/4\"", "10'-7\"")]   // rounds up
        // Whole-inch carry to feet.
        [InlineData("10'-11 1/2\"", "11'-0\"")]
        [InlineData("9'-11 3/4\"", "10'-0\"")]
        // Spaces around the foot-inch dash and trailing words/periods are normalized away.
        [InlineData("10' - 0\" CLG.", "10'-0\"")]
        // Leading '+' stripped.
        [InlineData("+10'-0\"", "10'-0\"")]
        // Feet-only value.
        [InlineData("10'", "10'-0\"")]
        // Decimal feet.
        [InlineData("10.5'", "10'-6\"")]
        public void Clean_ReturnsRoundedHeight(string input, string expectedHeight)
        {
            var (height, _) = CeilingHeightFormatter.Clean(input);
            Assert.Equal(expectedHeight, height);
        }

        [Theory]
        [InlineData("10'-0\" Vaulted", "10'-0\"", "VAULTED")]
        [InlineData("10'-6 1/2\" Tray", "10'-7\"", "TRAY")]
        // Descriptor with no measurement: height empty, description survives.
        [InlineData("Vaulted", "", "VAULTED")]
        public void Clean_SplitsDescriptor(string input, string expectedHeight, string expectedDescription)
        {
            var (height, description) = CeilingHeightFormatter.Clean(input);
            Assert.Equal(expectedHeight, height);
            Assert.Equal(expectedDescription, description);
        }

        [Theory]
        // No foot/inch mark → not a stampable height (bare digits dropped, per TurboName-7 decision).
        [InlineData("10", "")]
        [InlineData("", "")]
        public void Clean_DropsNonHeightNumbers(string input, string expectedHeight)
        {
            var (height, _) = CeilingHeightFormatter.Clean(input);
            Assert.Equal(expectedHeight ?? "", height ?? "");
        }

        [Theory]
        // Height-shaped: leads with a digit or '+' AND carries a '/". (sameLayer classifier — TurboName-8.)
        [InlineData("10'-0\"", true)]
        [InlineData("+10'-0\"", true)]
        [InlineData("9'-0\"", true)]
        [InlineData("8'-4\"", true)]
        [InlineData("10'", true)]          // foot mark only
        [InlineData("6\"", true)]          // inch mark only
        [InlineData("10'-0\" VAULTED", true)] // height + trailing descriptor still reads as a height
        // Numeric-leading room names have NO foot/inch mark → stay room names.
        [InlineData("1-CAR GARAGE", false)]
        [InlineData("3-CAR GARAGE", false)]
        [InlineData("2ND FLOOR MECH", false)]
        [InlineData("BEDROOM 4", false)]
        [InlineData("MECH 5", false)]
        // Non-numeric-leading text (incl. bare descriptors) → room name; descriptor keywords not consulted.
        [InlineData("MASTER BEDROOM", false)]
        [InlineData("VAULTED", false)]
        [InlineData("GRAND SITTING", false)] // contains "TIN" — must NOT be treated as a height/descriptor
        [InlineData("", false)]
        [InlineData("   ", false)]
        public void LooksLikeHeight_ClassifiesSharedLayerText(string input, bool expected)
        {
            Assert.Equal(expected, CeilingHeightFormatter.LooksLikeHeight(input));
        }

        [Theory]
        // Anything Clean() can emit: letters-only, keyword-bearing tokens, upper-cased.
        [InlineData("VAULTED", true)]
        [InlineData("TRAY", true)]
        [InlineData("SLOPED", true)]
        [InlineData("COFFERED", true)]
        [InlineData("SLOPED VAULTED", true)]        // multi-token: EVERY token qualifies
        [InlineData("SLOPED\rVAULTED", true)]       // TextNote.Text separates lines with a bare '\r'
        [InlineData("Vaulted", true)]               // case-insensitive — a note someone re-typed in mixed case
        // General-purpose AL_Annotation_3" text that must SURVIVE the clear.
        [InlineData("SEE DETAIL 4/A5.1", false)]    // digits + punctuation
        [InlineData("TYP.", false)]                 // trailing period is not a letter
        [InlineData("ALIGN", false)]                // letters-only but no keyword
        [InlineData("SLOPED CEILING", false)]       // CEILING carries no keyword — all-tokens, not any-token
        [InlineData("VAULTED 10'-0\"", false)]      // a height mark is not a description line
        [InlineData("2", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        public void LooksLikeDescriptionNote_MatchesOnlyGeneratedDescriptionText(string input, bool expected)
        {
            Assert.Equal(expected, CeilingHeightFormatter.LooksLikeDescriptionNote(input));
        }

        [Theory]
        // Round-trip guard: whatever Clean() actually emits as a description MUST be recognized by
        // LooksLikeDescriptionNote, or the clear would orphan the notes it just placed. Pins the two
        // functions together so a future edit to PreservedWords or Clean's tokenizer can't split them.
        [InlineData("10'-0\" Vaulted")]
        [InlineData("10'-6 1/2\" Tray")]
        [InlineData("Vaulted")]
        [InlineData("9'-0\" SLOPED VAULT")]
        public void LooksLikeDescriptionNote_AcceptsEverythingCleanEmits(string rawCadValue)
        {
            var (_, description) = CeilingHeightFormatter.Clean(rawCadValue);
            Assert.NotEqual("", description);
            Assert.True(CeilingHeightFormatter.LooksLikeDescriptionNote(description),
                $"Clean(\"{rawCadValue}\") emitted \"{description}\", which the clear would not recognize.");
        }
    }
}
