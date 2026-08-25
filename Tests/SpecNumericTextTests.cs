using TurboSuite.Schedule.Services;
using Xunit;

namespace TurboSuite.Tests.Schedule
{
    /// <summary>
    /// Pins the workbook⇄model numeric reconciliation. The load-bearing property is that an untouched
    /// numeric cell keys equal to the model display it was seeded from (no spurious re-write), while
    /// length/compound units fall back to verbatim string comparison.
    /// </summary>
    public class SpecNumericTextTests
    {
        // ── TryBare: scalar units succeed, lengths/compounds fall back to verbatim ──

        [Theory]
        [InlineData("32 W", "32")]
        [InlineData("277 V", "277")]
        [InlineData("32.00 W", "32.00")]
        [InlineData("85 %", "85")]
        [InlineData("3500 K", "3500")]
        [InlineData("24°", "24")]
        [InlineData("90", "90")]        // unitless integer (e.g. CRI)
        [InlineData("-5 W", "-5")]
        [InlineData("12 W/ft", "12")]   // pure ratio unit (Power/Length)
        [InlineData("110 lm/W", "110")] // pure ratio unit (Efficacy)
        public void TryBare_Scalar_Succeeds(string display, string expectedBare)
        {
            Assert.True(SpecNumericText.TryBare(display, out var bare));
            Assert.Equal(expectedBare, bare);
        }

        [Theory]
        [InlineData("3\"")]             // inch mark
        [InlineData("0' - 3\"")]        // feet-inches
        [InlineData("1 1/2\"")]         // fractional inches
        public void TryBare_LengthOrCompound_FallsBackToVerbatim(string display)
        {
            Assert.False(SpecNumericText.TryBare(display, out var bare));
            Assert.Equal(display.Trim(), bare);
        }

        [Fact]
        public void TryBare_Blank_IsTrivialSuccess()
        {
            Assert.True(SpecNumericText.TryBare("   ", out var bare));
            Assert.Equal("", bare);
        }

        // ── CompareKey: the anti-spurious-write property ──

        [Theory]
        [InlineData("32", "32 W")]      // bare cell vs unit-ful model
        [InlineData("32", "32.00 W")]   // display precision difference
        [InlineData("32.0", "32 W")]
        [InlineData("1000", "1,000 lm")] // thousands separator noise
        [InlineData("277", "277 V")]
        [InlineData("95", "95 lm/W")]   // bare ratio cell vs unit-ful model (Efficacy round-trip)
        [InlineData("12", "12 W/ft")]   // bare ratio cell vs unit-ful model (Power/Length)
        public void CompareKey_UnchangedScalar_KeysEqual(string cell, string modelDisplay)
        {
            Assert.Equal(SpecNumericText.CompareKey(modelDisplay), SpecNumericText.CompareKey(cell));
        }

        [Theory]
        [InlineData("40", "32 W")]
        [InlineData("32.5", "32 W")]
        public void CompareKey_ChangedScalar_KeysDiffer(string cell, string modelDisplay)
        {
            Assert.NotEqual(SpecNumericText.CompareKey(modelDisplay), SpecNumericText.CompareKey(cell));
        }

        [Fact]
        public void CompareKey_UnchangedLength_VerbatimKeysEqual()
        {
            Assert.Equal(SpecNumericText.CompareKey("3\""), SpecNumericText.CompareKey("3\""));
        }

        [Fact]
        public void CompareKey_ChangedLength_KeysDiffer()
        {
            Assert.NotEqual(SpecNumericText.CompareKey("3\""), SpecNumericText.CompareKey("4\""));
        }

        // ── SeedCell: what a freshly-appended cell holds ──

        [Theory]
        [InlineData("32 W", "32")]
        [InlineData("0 lm/W", "0")]     // ratio unit seeds bare (Efficacy — no lm/W to type)
        [InlineData("3\"", "3\"")]      // length seeds verbatim
        [InlineData("", "")]
        public void SeedCell_BaresScalar_KeepsVerbatimLength(string display, string expected)
        {
            Assert.Equal(expected, SpecNumericText.SeedCell(display));
        }
    }
}
