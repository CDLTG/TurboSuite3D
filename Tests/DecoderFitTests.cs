using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Single-decoder fit against the DMX-4-5000-10A caps (C1 ≤10 A/color, C2 ≤960 W, 4 outputs).
    /// Caps are Tier B (datasheet); verdicts are Tier A arithmetic.
    /// </summary>
    public class DecoderFitTests
    {
        private static readonly DecoderSpec Decoder = DecoderSpec.Dmx4_5000_10A;
        private const double Volts = 24.0;

        [Fact]
        public void Dec20_FitsOneDecoder_BothCapsSlack()
        {
            var run = new TapeRun(94.3, 5.2, channels: 4); // 490 W RGBW
            var result = DecoderFit.Check(Decoder, run, Volts);

            Assert.True(result.Fits);
            Assert.True(result.WithinPerColorCurrent);
            Assert.True(result.WithinTotalWatts);
        }

        [Fact]
        public void WhiteHeavyRun_FailsC1Only_NotC2()
        {
            // Single-color (1 channel) concentrates all watts on one terminal: 300 W ⇒ 12.5 A (>10),
            // while 300 W ≪ 960. The only way C1 binds independently (the split seam).
            var run = new TapeRun(60.0, 5.0, channels: 1); // 300 W
            var result = DecoderFit.Check(Decoder, run, Volts);

            Assert.False(result.Fits);
            Assert.False(result.WithinPerColorCurrent);
            Assert.True(result.WithinTotalWatts);
            Assert.Equal(12.5, result.PerColorAmps, precision: 2);
        }

        [Fact]
        public void EvenRgbw_C1AndC2ConvergeAt960W()
        {
            var run = new TapeRun(184.615384615, 5.2, channels: 4); // 960 W
            var result = DecoderFit.Check(Decoder, run, Volts);

            Assert.Equal(960.0, result.TotalWatts, precision: 1);
            Assert.Equal(10.0, result.PerColorAmps, precision: 3);
            Assert.True(result.Fits);
        }

        [Fact]
        public void OversizedRgbw_FailsBothCapsTogether()
        {
            var run = new TapeRun(200.0, 5.2, channels: 4); // 1040 W
            var result = DecoderFit.Check(Decoder, run, Volts);

            Assert.False(result.Fits);
            Assert.False(result.WithinPerColorCurrent);
            Assert.False(result.WithinTotalWatts);
        }

        [Fact]
        public void TapeNeedingMoreChannelsThanOutputs_FailsOnOutputs()
        {
            // 5-channel RGBTW on the 4-output decoder: even at trivial watts, the OUTPUT cap fails —
            // this is what the solver turns into the run-breaking blocker.
            var run = new TapeRun(10.0, 5.2, channels: 5);
            var result = DecoderFit.Check(Decoder, run, Volts);

            Assert.False(result.WithinOutputs);
        }
    }
}
