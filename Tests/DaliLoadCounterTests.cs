using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Input;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliLoadCounter"/> — loads-per-zone counted by DALI address (circuit), not by
    /// fixture. The shared-driver tape case is the whole reason this exists: six tape runs on one circuit are
    /// one address = one load, while a downlight on its own circuit stays one. The driver device never
    /// reaches this counter (the shim feeds fixtures only), so it needs no representation here.
    /// </summary>
    public class DaliLoadCounterTests
    {
        private static DaliFixtureReading On(string circuit, string zone) => new DaliFixtureReading(circuit, zone);
        private static DaliFixtureReading Loose(string zone) => new DaliFixtureReading("", zone);

        [Fact]
        public void SixTapeRunsOnOneCircuit_CollapseToOneLoad()
        {
            var readings = Enumerable.Range(0, 6).Select(_ => On("C1", "Kitchen"));

            var byZone = DaliLoadCounter.CountByZone(readings);

            Assert.Equal(1, byZone["Kitchen"]);
        }

        [Fact]
        public void DownlightsEachOnOwnCircuit_CountIndividually()
        {
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                On("C1", "Hall"), On("C2", "Hall"), On("C3", "Hall"),
            });

            Assert.Equal(3, byZone["Hall"]);   // three circuits = three addresses
        }

        [Fact]
        public void TapeAndDownlightsInSameZone_SumByCircuit()
        {
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                On("Tape", "Living"), On("Tape", "Living"), On("Tape", "Living"),  // 1 address
                On("Dl1", "Living"), On("Dl2", "Living"),                          // 2 addresses
            });

            Assert.Equal(3, byZone["Living"]);
        }

        [Fact]
        public void UncircuitedFixture_IsItsOwnLoad()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { Loose("Bath"), Loose("Bath") });

            Assert.Equal(2, byZone["Bath"]);   // not yet grouped ⇒ conservative one each
        }

        [Fact]
        public void CircuitZone_ResolvesFromFirstNonBlankFixture()
        {
            // A driver-fed run where the lead fixture's zone didn't read but a sibling on the circuit carries it.
            var byZone = DaliLoadCounter.CountByZone(new[]
            {
                On("C1", ""), On("C1", "Loft"), On("C1", ""),
            });

            Assert.Equal(1, byZone["Loft"]);
        }

        [Fact]
        public void AllBlankCircuit_AddsNoLoad()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { On("C1", ""), On("C1", "") });

            Assert.Empty(byZone);   // an unassigned address contributes nothing to any zone
        }

        [Fact]
        public void ZoneMatchingIsCaseInsensitive()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { On("C1", "Kitchen"), On("C2", "KITCHEN") });

            var only = Assert.Single(byZone);
            Assert.Equal(2, only.Value);   // same zone, two circuits
        }

        [Fact]
        public void SeparateZones_StayApart()
        {
            var byZone = DaliLoadCounter.CountByZone(new[] { On("C1", "A"), On("C2", "B") });

            Assert.Equal(1, byZone["A"]);
            Assert.Equal(1, byZone["B"]);
        }

        [Fact]
        public void NullInput_YieldsEmpty()
            => Assert.Empty(DaliLoadCounter.CountByZone(null));
    }
}
