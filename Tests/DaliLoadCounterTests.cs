using TurboSuite.Dali.Input;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliLoadCounter"/> — loads-per-zone counted by <b>addressable unit = one DALI
    /// address</b> (a driver device or a self-driven downlight), not by circuit. This is the per-unit fix for
    /// the 64/bus warning: a circuit carrying N drivers is N addresses, so it must count as N — the exact
    /// opposite of the old circuit-collapse. The unit enumeration (which decides driver-vs-downlight per
    /// circuit) is the shim's <c>DaliUnitEnumerator</c>; here the counter is a flat per-zone tally, so these
    /// oracles pin the tally, not the enumeration.
    /// </summary>
    public class DaliLoadCounterTests
    {
        private static int _seq;

        private static DaliUnitReading Driver(string zone) =>
            new DaliUnitReading("k" + _seq++, "C", DaliUnitKind.Driver, 0, zone, null);

        private static DaliUnitReading Downlight(string zone) =>
            new DaliUnitReading("k" + _seq++, "C", DaliUnitKind.Downlight, 0, zone, null);

        [Fact]
        public void ThreeDriversInOneZone_CountAsThree()
        {
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                Driver("cove"), Driver("cove"), Driver("cove"),
            });

            Assert.Equal(3, byZone["cove"]);   // three driver devices = three DALI addresses
        }

        [Fact]
        public void DownlightsEachCountOne()
        {
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                Downlight("Hall"), Downlight("Hall"), Downlight("Hall"),
            });

            Assert.Equal(3, byZone["Hall"]);
        }

        [Fact]
        public void MixedZone_SumsDriversAndDownlights()
        {
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                Driver("Living"), Driver("Living"), Driver("Living"),   // 3 addresses
                Downlight("Living"), Downlight("Living"),               // 2 addresses
            });

            Assert.Equal(5, byZone["Living"]);
        }

        [Fact]
        public void BlankZoneUnit_AddsNoLoad()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { Driver(""), Downlight("") });

            Assert.Empty(byZone);   // present hardware, but unzoned ⇒ joins no loop
        }

        [Fact]
        public void ZoneMatchingIsCaseInsensitive()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { Driver("Kitchen"), Driver("KITCHEN") });

            var only = Assert.Single(byZone);
            Assert.Equal(2, only.Value);
        }

        [Fact]
        public void SeparateZones_StayApart()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { Driver("A"), Downlight("B") });

            Assert.Equal(1, byZone["A"]);
            Assert.Equal(1, byZone["B"]);
        }

        [Fact]
        public void NullInput_YieldsEmpty()
            => Assert.Empty(DaliLoadCounter.CountByZone(null));
    }
}
