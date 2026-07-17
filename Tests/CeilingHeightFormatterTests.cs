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
    }
}
