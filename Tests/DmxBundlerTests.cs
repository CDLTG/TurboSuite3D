using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxBundler"/> — coalescing per-fixture runs into field-connectable chains
    /// ("bundles"). The motivating case: 204 stacked 17.2 W light sheets that can only chain 5-per-tap,
    /// so the packer must see 86 W chains, not 17.2 W sheets.
    /// </summary>
    public class DmxBundlerTests
    {
        // A point-sheet reading as the reader emits it: unit length, watts as W/ft, 2 channels.
        private static DmxBundler.Item Sheet(long id, int max = 5, double watts = 17.2, string mark = "SHEET") =>
            new DmxBundler.Item(id, new TapeRun(1.0, watts, 2), max, mark);

        private static double Watts(TapeRun r) => PowerMath.TotalWatts(r);

        [Fact]
        public void SeventyTwoSheetsAtMaxFive_YieldFifteenBundles_14x86W_Plus1x34W()
        {
            var items = Enumerable.Range(1, 72).Select(i => Sheet(i)).ToList();

            var bundles = DmxBundler.Bundle(items);

            Assert.Equal(15, bundles.Count);                        // 14×5 + 1×2
            Assert.Equal(14, bundles.Count(b => b.LengthFt == 5.0));
            Assert.All(bundles.Take(14), b => Assert.Equal(86.0, Watts(b), 3));
            Assert.Equal(2.0, bundles[14].LengthFt, 6);             // remainder chain of 2
            Assert.Equal(34.4, Watts(bundles[14]), 3);
        }

        [Fact]
        public void SixtySheetsAtMaxFive_YieldTwelveFullBundles()
        {
            var bundles = DmxBundler.Bundle(Enumerable.Range(1, 60).Select(i => Sheet(i)));

            Assert.Equal(12, bundles.Count);
            Assert.All(bundles, b => Assert.Equal(86.0, Watts(b), 3));
        }

        [Fact]
        public void MaxOne_LeavesEveryRunUnbundled()
        {
            var items = Enumerable.Range(1, 7).Select(i => Sheet(i, max: 1)).ToList();

            var bundles = DmxBundler.Bundle(items);

            Assert.Equal(7, bundles.Count);
            Assert.All(bundles, b => { Assert.Equal(1.0, b.LengthFt, 6); Assert.Equal(17.2, Watts(b), 3); });
        }

        [Fact]
        public void MaxZeroOrNegative_ClampsToOne()
        {
            var bundles = DmxBundler.Bundle(new[] { Sheet(1, max: 0), Sheet(2, max: -3) });
            Assert.Equal(2, bundles.Count);
        }

        [Fact]
        public void MixedWattsInOneProduct_SumExactly()
        {
            var items = new[]
            {
                new DmxBundler.Item(1, new TapeRun(1.0, 17.2, 2), 5, "SHEET"),
                new DmxBundler.Item(2, new TapeRun(1.0, 17.2, 2), 5, "SHEET"),
                new DmxBundler.Item(3, new TapeRun(1.0, 20.0, 2), 5, "SHEET"),
            };

            var bundle = Assert.Single(DmxBundler.Bundle(items));
            Assert.Equal(3.0, bundle.LengthFt, 6);
            Assert.Equal(54.4, Watts(bundle), 3);                   // 17.2 + 17.2 + 20.0, preserved exactly
            Assert.Equal(2, bundle.Channels);
        }

        [Fact]
        public void DifferentTypeMarks_NeverChainTogether()
        {
            var items = new[] { Sheet(1, mark: "A"), Sheet(2, mark: "A"), Sheet(3, mark: "A"),
                                Sheet(4, mark: "B"), Sheet(5, mark: "B"), Sheet(6, mark: "B") };

            var bundles = DmxBundler.Bundle(items);

            Assert.Equal(2, bundles.Count);                         // one per product, NOT a chain of 5+1
            Assert.All(bundles, b => Assert.Equal(3.0, b.LengthFt, 6));
        }

        [Fact]
        public void DifferentChannelCounts_NeverChainTogether()
        {
            var items = new[]
            {
                new DmxBundler.Item(1, new TapeRun(1.0, 10.0, 2), 5, "X"),
                new DmxBundler.Item(2, new TapeRun(1.0, 10.0, 4), 5, "X"),
            };

            Assert.Equal(2, DmxBundler.Bundle(items).Count);
        }

        [Fact]
        public void SlicingIsDeterministicByElementId_RegardlessOfInputOrder()
        {
            // Length = id makes the slice boundary observable: sorted [1..5] sums to 15, [6] to 6.
            var shuffled = new[] { 6L, 5, 4, 3, 2, 1 }
                .Select(i => new DmxBundler.Item(i, new TapeRun(i, 1.0, 2), 5, "SHEET"));

            var bundles = DmxBundler.Bundle(shuffled);

            Assert.Equal(2, bundles.Count);
            Assert.Equal(15.0, bundles[0].LengthFt, 6);             // ids 1+2+3+4+5
            Assert.Equal(6.0, bundles[1].LengthFt, 6);             // id 6 alone
        }

        [Fact]
        public void CountBundles_MatchesBundleCount()
        {
            var items = Enumerable.Range(1, 204).Select(i => Sheet(i)).ToList();
            Assert.Equal(DmxBundler.Bundle(items).Count, DmxBundler.CountBundles(items));
        }
    }
}
