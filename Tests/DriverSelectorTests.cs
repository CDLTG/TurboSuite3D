using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Step 4 oracle — driver selection with derate as a contract INPUT (not a hardcoded default).
    /// DEC 20 (490 W on a 600 W "ME") is the threshold case: it fits one driver iff derate ≥ 490/600.
    ///
    /// CONFIDENCE: Tier A. Every verdict is arithmetic over the (input) derate; no magic % to confirm.
    /// </summary>
    public class DriverSelectorTests
    {
        private const double V = 24.0;

        // The foyer job's three driver Types @24 V. Derate is supplied per-test (the input under study),
        // so these carry raw 0 (= no derate) and the test overrides via the candidate list when needed.
        private static DriverType[] Family(double derate) => new[]
        {
            new DriverType("320", 320.0, V, derate),
            new DriverType("480", 480.0, V, derate), // "MD"
            new DriverType("600", 600.0, V, derate), // "ME"
        };

        [Theory]
        [InlineData(0.90, "600")]      // 600×0.90=540 ≥ 490 ⇒ fits ME
        [InlineData(0.81667, "600")]   // 600×0.81667=490.0 ≥ 490 ⇒ boundary fits (inclusive)
        [InlineData(0.81, null)]       // 600×0.81=486 < 490 ⇒ no single driver fits ⇒ split
        [InlineData(0.80, null)]       // 600×0.80=480 < 490 ⇒ the as-built case the 80% placeholder wrongly rejected
        public void Dec20_FitsOneMe_IffDerateAtLeastThreshold(double derate, string? expectedName)
        {
            var pick = DriverSelector.SelectSmallestFitting(Family(derate), loadWatts: 490.0, systemVolts: V);

            if (expectedName is null)
                Assert.Null(pick);
            else
                Assert.Equal(expectedName, pick!.Value.Name);
        }

        [Fact]
        public void SelectsSmallestThatFits_NotJustAnyThatFits()
        {
            // 300 W at no-derate: 320 is the smallest cap ≥ 300, so 320 — not 480 or 600.
            var pick = DriverSelector.SelectSmallestFitting(Family(1.0), loadWatts: 300.0, systemVolts: V);
            Assert.Equal("320", pick!.Value.Name);
        }

        [Fact]
        public void FiltersOutVoltageMismatchedTypes()
        {
            // A big 48 V driver must never be chosen for a 24 V system, even though it'd "fit" on watts.
            var mixed = new[]
            {
                new DriverType("600@24", 600.0, 24.0, 1.0),
                new DriverType("2000@48", 2000.0, 48.0, 1.0),
            };
            var pick = DriverSelector.SelectSmallestFitting(mixed, loadWatts: 700.0, systemVolts: 24.0);

            Assert.Null(pick); // 600@24 too small, 2000@48 wrong voltage ⇒ split, never the 48 V part
        }

        [Fact]
        public void UnsetDerate_BehavesAsNoDerate()
        {
            // Raw 0 ⇒ 1.0, so 600 W rated fully covers a 590 W load.
            var pick = DriverSelector.SelectSmallestFitting(Family(0.0), loadWatts: 590.0, systemVolts: V);
            Assert.Equal("600", pick!.Value.Name);
        }

        [Theory]
        [InlineData(0.80, 480.0)]  // largest 600 × 0.80
        [InlineData(0.85, 510.0)]  // largest 600 × 0.85
        [InlineData(1.00, 600.0)]  // no derate
        public void LargestEffectiveCap_DrivesTheDecoderCouplingBound(double derate, double expected)
        {
            // This is the value Step 5 will clamp the decoder ceiling to: min(960, this).
            Assert.Equal(expected, DriverSelector.LargestEffectiveCap(Family(derate), V), precision: 6);
        }
    }
}
