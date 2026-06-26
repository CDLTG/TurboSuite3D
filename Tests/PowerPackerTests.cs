using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The coupling + end-to-end power pass (runs → decoders → drivers) for one selected decoder type.
    /// The derate INPUT drives the count via the coupled cap min(decoder cap, largest driver × derate).
    /// </summary>
    public class PowerPackerTests
    {
        private static readonly DecoderSpec Decoder = DecoderSpec.Dmx4_5000_10A;
        private const double V = 24.0;

        private static DriverType[] Family(double derate) => new[]
        {
            new DriverType("320", 320.0, V, derate),
            new DriverType("480", 480.0, V, derate),
            new DriverType("600", 600.0, V, derate),
        };

        [Theory]
        [InlineData(1.00, 600.0)]  // min(960, 600×1.00)
        [InlineData(0.85, 510.0)]  // min(960, 600×0.85)
        [InlineData(0.80, 480.0)]  // min(960, 600×0.80) — driver governs, not the 960 decoder cap
        public void CoupledCap_IsDriverBound_NotDecoderBound(double derate, double expected)
        {
            Assert.Equal(expected, PowerPacker.CoupledDecoderCap(Decoder, channels: 4, V, Family(derate)), precision: 6);
        }

        [Fact]
        public void Dec20_AtDerate85_IsOneDecoderOneMe()
        {
            var runs = new[] { new TapeRun(94.3, 5.2, 4) }; // 490 W
            var result = PowerPacker.Pack(runs, Decoder, V, Family(0.85)); // coupled cap 510 ≥ 490

            Assert.Equal(1, result.DecoderCount);
            Assert.Equal("600", result.Decoders[0].Driver.Name);
        }

        [Fact]
        public void Dec20_AtDerate80_IsOverCap_BecauseCoupledCapDropsBelowLoad()
        {
            // 490 W > coupled cap 480 @0.80. Under the drawn-correctly contract (Design §0b) this is a
            // redraw flag, not a split — Pack's backstop throws (DmxValidator surfaces it upstream).
            var runs = new[] { new TapeRun(94.3, 5.2, 4) }; // 490 W
            Assert.Throws<System.InvalidOperationException>(() => PowerPacker.Pack(runs, Decoder, V, Family(0.80)));
        }

        private static void AssertPackIsSound(IReadOnlyList<TapeRun> runs, PowerPackResult result)
        {
            Assert.Equal(runs.Sum(PowerMath.TotalWatts), result.Decoders.Sum(d => d.Decoder.TotalWatts), precision: 6);
            Assert.All(result.Decoders, d =>
            {
                Assert.True(d.Decoder.TotalWatts <= result.CoupledDecoderCapWatts + 1e-6);
                Assert.True(d.Driver.EffectiveWattCap >= d.Decoder.TotalWatts - 1e-6);
            });
            Assert.Equal(result.DecoderCount, result.DriverCount);
        }

        [Fact]
        public void Derate_TightensTheCap_LooseFitsOnePerRun_TightFlagsOverCap()
        {
            // The derate INPUT moves the cap the designer must draw under — not a split count. At 1.00 the
            // 500 W runs each fit one whole; at 0.80 the cap drops to 480 < 500 ⇒ over-cap, so Pack's
            // backstop throws (these would be redraw flags, Design §0b), never a silent split.
            var runs = Enumerable.Range(0, 10).Select(_ => new TapeRun(500.0 / 5.2, 5.2, 4)).ToArray();

            var loose = PowerPacker.Pack(runs, Decoder, V, Family(1.00));
            Assert.Equal(10, loose.DecoderCount); // cap 600, each 500 fits one whole
            AssertPackIsSound(runs, loose);

            Assert.Throws<System.InvalidOperationException>(() => PowerPacker.Pack(runs, Decoder, V, Family(0.80)));
        }

        [Fact]
        public void EmergentFloor_ConstructedAnalogOf208Case()
        {
            // Constructed analog (real RunLengths is local-only): 53 × 475 W = 25,175 W. At derate 0.80
            // the coupled cap is 480, each 475 fits one decoder ⇒ exactly 53 — the ceil(total/480) floor.
            var runs = Enumerable.Range(0, 53).Select(_ => new TapeRun(475.0 / 5.2, 5.2, 4)).ToArray();
            var result = PowerPacker.Pack(runs, Decoder, V, Family(0.80));

            Assert.Equal(53, result.DecoderCount);
            AssertPackIsSound(runs, result);
        }
    }
}
