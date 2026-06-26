using System;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The 120 V feed pass (§0c): pack drivers onto breakers by CONNECTED LOAD watts under two co-equal
    /// limits — the watt cap (amps × volts × continuous-derate) and the inrush count cap. Verified by
    /// invariants (both floors, conservation, both caps respected), never an unverifiable exact count.
    /// </summary>
    public class BreakerPackerTests
    {
        [Fact]
        public void Cap_Is20A_120V_80pct_Equals1920W()
            => Assert.Equal(1920.0, BreakerPacker.Cap(20, 120, 0.8), precision: 6);

        [Fact]
        public void FewLargeDrivers_AreWattBound()
        {
            // 5 × 600 W = 3000 W under a 1920 W cap ⇒ ceil(3000/1920) = 2 (three fit one at 1800 W).
            var loads = Enumerable.Repeat(600.0, 5).ToList();
            Assert.Equal(2, BreakerPacker.Count(loads, cap: 1920, maxPerBreaker: 0));
        }

        [Fact]
        public void ManySmallDrivers_AreCountBound_NotWattBound()
        {
            // 10 × 52 W = 520 W ≪ 1920 (one breaker by watts), but inrush caps at 4/breaker ⇒ 3 breakers.
            // This is the case the user called out: a 52 W driver draws 52 W, so watts never bind here.
            var loads = Enumerable.Repeat(52.0, 10).ToList();
            Assert.Equal(3, BreakerPacker.Count(loads, cap: 1920, maxPerBreaker: 4));
        }

        [Fact]
        public void Pack_RespectsBothFloors_AndConservesWatts()
        {
            var loads = new[] { 600.0, 480, 300, 300, 200, 120, 90, 52, 52, 52 };
            const double cap = 1920; const int maxPer = 4;

            var bins = BreakerPacker.Pack(loads, cap, maxPer);

            int wattFloor = (int)Math.Ceiling(loads.Sum() / cap);
            int countFloor = (int)Math.Ceiling(loads.Length / (double)maxPer);
            Assert.True(bins.Count >= Math.Max(wattFloor, countFloor));
            Assert.All(bins, b => Assert.True(b.TotalWatts <= cap + 1e-6, $"breaker {b.TotalWatts} W over cap"));
            Assert.All(bins, b => Assert.True(b.DriverCount <= maxPer, $"breaker {b.DriverCount} drivers over inrush cap"));
            Assert.Equal(loads.Sum(), bins.Sum(b => b.TotalWatts), precision: 6); // conservation
        }

        [Fact]
        public void NoCountCap_IsPurelyWattBound()
        {
            var loads = Enumerable.Repeat(52.0, 30).ToList(); // 1560 W ≤ 1920, no inrush limit ⇒ 1 breaker
            Assert.Equal(1, BreakerPacker.Count(loads, cap: 1920, maxPerBreaker: 0));
        }

        [Fact]
        public void BreakerBasis_DriverRating_PacksNameplate_NotLoad()
        {
            // 9 zones, each one lightly-loaded 600 W-rated driver (100 W of actual load). By connected
            // load: 9 × 100 = 900 W ⇒ 1 breaker. By nameplate: 9 × 600 = 5400 W ⇒ ⌈5400/1920⌉ = 3.
            var zones = Enumerable.Range(0, 9)
                .Select(i => new ZoneDesign($"z{i}", new[] { new TapeRun(100.0, 1.0, 2) })).ToArray();
            DriverType[] meOnly = { new DriverType("ME", 600.0, 24.0, 1.0) };
            DecoderSpec[] pool = { DecoderSpec.Dmx4_5000_10A };

            DmxBill Solve(BreakerBasis b) => DmxSolver.Solve(new DmxContract(
                pool, meOnly, 24.0, 512, 0, 32, 20, 120, 0.8, 0, b), zones);

            Assert.Equal(1, Solve(BreakerBasis.ConnectedLoad).RequiredBreakers);
            Assert.Equal(3, Solve(BreakerBasis.DriverRating).RequiredBreakers);
        }

        [Fact]
        public void DriverOverBreakerCap_Throws()
        {
            // A breaker too small for a single driver's load — config error, not a silent split.
            Assert.Throws<InvalidOperationException>(() => BreakerPacker.Pack(new[] { 600.0 }, cap: 480, maxPerBreaker: 0));
        }
    }
}
