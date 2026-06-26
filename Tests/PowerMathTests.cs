using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// DEC 20, the foyer-job worked power check (§5): 94.3 ft of 4-channel (RGBW) tape @ 5.2 W/ft, 24 V.
    /// TotalWatts/PerColorAmps are Tier-A arithmetic; the per-color value rests on the even split (÷ channels).
    /// </summary>
    public class PowerMathTests
    {
        private static readonly TapeRun Dec20 = new TapeRun(lengthFt: 94.3, wattsPerFt: 5.2, channels: 4);
        private const double OperatingVolts = 24.0;

        [Fact]
        public void Dec20_TotalWatts_Is490()
        {
            Assert.Equal(490.0, PowerMath.TotalWatts(Dec20), precision: 0);
        }

        [Fact]
        public void Dec20_PerColorAmps_IsAbout5Point1_UnderEvenSplit()
        {
            // 490 W / 24 V / 4 channels ≈ 5.11 A — comfortably under the decoder's 10 A C1 cap.
            Assert.Equal(5.11, PowerMath.PerColorAmps(Dec20, OperatingVolts), precision: 2);
        }
    }

    /// <summary>
    /// Derate is a contract INPUT (the family Derating Factor param), normalized by the documented
    /// missing/0/out-of-range ⇒ no-derate rule. See DriverSelectorTests / ORACLES.md.
    /// </summary>
    public class DriverDerateNormalizationTests
    {
        [Theory]
        [InlineData(0.80, 0.80)]
        [InlineData(0.85, 0.85)]
        [InlineData(1.00, 1.00)]
        [InlineData(0.00, 1.00)]   // unset/0 ⇒ no derate
        [InlineData(-0.5, 1.00)]   // negative ⇒ no derate
        [InlineData(1.50, 1.00)]   // out of range ⇒ no derate
        public void Normalize_AppliesMissingOrOutOfRangeMeansNoDerate(double raw, double expected)
        {
            Assert.Equal(expected, DeratingFactor.Normalize(raw), precision: 6);
        }
    }
}
