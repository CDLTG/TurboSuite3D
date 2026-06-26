using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Packing a zone's runs into decoders (decoder-only caps). Small exact cases from the doc's
    /// worked examples; the big/emergent case is checked by INVARIANTS, never an unverifiable count.
    /// </summary>
    public class DecoderPackerTests
    {
        private static readonly DecoderSpec Decoder = DecoderSpec.Dmx4_5000_10A;
        private const double Volts = 24.0;

        private static void AssertConservesWatts(IReadOnlyList<TapeRun> runs, IReadOnlyList<DecoderLoad> loads)
            => Assert.Equal(runs.Sum(PowerMath.TotalWatts), loads.Sum(l => l.TotalWatts), precision: 6);

        private static void AssertEveryLoadWithinCap(IReadOnlyList<DecoderLoad> loads, int channels)
        {
            double cap = DecoderPacker.EffectiveWattCap(Decoder, channels, Volts);
            Assert.All(loads, l => Assert.True(l.TotalWatts <= cap + 1e-6, $"load {l.TotalWatts:F1} W exceeds cap {cap:F1} W"));
        }

        [Fact]
        public void Dec20_SingleRun_PacksToOneDecoder()
        {
            var runs = new[] { new TapeRun(94.3, 5.2, channels: 4) }; // 490 W
            var loads = DecoderPacker.Pack(runs, Decoder, Volts);

            Assert.Single(loads);
            Assert.Equal(490.0, loads[0].TotalWatts, precision: 0);
            AssertConservesWatts(runs, loads);
            AssertEveryLoadWithinCap(loads, 4);
        }

        [Fact]
        public void FoyerCove_ThreeRings_PowerMinimalPackIsOneDecoder()
        {
            // 794 W ≤ 960, 8.3 A/color ≤ 10. As-built used 3 decoders for GEOMETRY (out of scope §3b);
            // the engine's power-minimal answer is 1 — intended divergence.
            var runs = new[]
            {
                new TapeRun(66.75,   5.2, 4),
                new TapeRun(44.0833, 5.2, 4),
                new TapeRun(42.0,    5.2, 4),
            };
            var loads = DecoderPacker.Pack(runs, Decoder, Volts);

            Assert.Single(loads);
            AssertConservesWatts(runs, loads);
            AssertEveryLoadWithinCap(loads, 4);
        }

        [Fact]
        public void OverCapRun_Throws_NotSilentlySplit()
        {
            // Drawn-correctly contract (Design §0b): a run over the cap is a redraw flag, never a split.
            var runs = new[] { new TapeRun(200.0, 5.2, 4) }; // 1040 W > 960 decoder cap
            Assert.Throws<System.InvalidOperationException>(() => DecoderPacker.Pack(runs, Decoder, Volts));
        }

        [Fact]
        public void SingleColor_CapIs240_NotImpliedBy960WattRating()
        {
            // C1 flows into packing: single-color cap = 10 A × 24 V × 1 = 240 W. Two 200 W runs can't share.
            Assert.Equal(240.0, DecoderPacker.EffectiveWattCap(Decoder, 1, Volts), precision: 6);

            var runs = new[] { new TapeRun(40.0, 5.0, 1), new TapeRun(40.0, 5.0, 1) }; // 200 W each
            var loads = DecoderPacker.Pack(runs, Decoder, Volts);

            Assert.Equal(2, loads.Count);
            AssertEveryLoadWithinCap(loads, 1);
            AssertConservesWatts(runs, loads);
        }

        [Fact]
        public void ManyRuns_SatisfyInvariants_WithoutAssertingExactCount()
        {
            var runs = Enumerable.Range(1, 50).Select(i => new TapeRun(20.0 + (i % 7) * 8.0, 5.2, 4)).ToArray();
            var loads = DecoderPacker.Pack(runs, Decoder, Volts);

            AssertConservesWatts(runs, loads);
            AssertEveryLoadWithinCap(loads, 4);
            int floor = (int)System.Math.Ceiling(runs.Sum(PowerMath.TotalWatts) / DecoderPacker.EffectiveWattCap(Decoder, 4, Volts));
            Assert.True(loads.Count >= floor);
        }
    }
}
