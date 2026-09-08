using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using Xunit;

namespace TurboSuite.Tests.Driver
{
    /// <summary>
    /// Oracle for the TurboDriver / TurboRPS "least overkill" tie-breaker: among driver types
    /// that pack a load equally well (same physical count, same total sub-drivers), the smallest
    /// -rated unit that still fits must win. Regression guard for a 30 W circuit landing on a
    /// 1000 W transformer purely because "1000W" sorts before "100W" in the candidate list.
    ///
    /// The transformer ladder is the worst case: nine single-unit drivers (Power == Sub-Driver
    /// Power ⇒ SubDriverCount = 1) spanning 100–1000 W, so a small load ties on every criterion
    /// above the new one.
    /// </summary>
    public class DriverSelectionServiceTests
    {
        private const string Volts = "24";
        private const string Protocol = "0-10V";
        private const string Mfr = "ACME";

        private static readonly double[] TransformerRatings =
            { 100, 150, 250, 300, 500, 600, 750, 900, 1000 };

        /// <summary>
        /// The nine-type transformer family, deliberately handed to the selector in the same
        /// pathological order the shim collector produces — lexical by type name, so "1000W"
        /// comes first. A robust tie-breaker must not depend on this order.
        /// </summary>
        private static List<DriverCandidateInfo> TransformerLadder(double derate = 1.0)
        {
            // Lexical order of "<rating>W": digit-by-digit, so 1000 sorts before 100, etc.
            return TransformerRatings
                .OrderBy(r => $"{r:0}W", System.StringComparer.Ordinal)
                .Select(r => new DriverCandidateInfo
                {
                    SymbolRef = new ElementRef((long)r),
                    FamilyTypeName = $"{r:0}W",
                    FamilyName = "XFMR",
                    CatalogNumber = $"XFMR-{r:0}",
                    Manufacturer = Mfr,
                    TotalPower = r,
                    SubDriverPower = r,       // transformer: one unit, no sub-drivers
                    SubDriverCount = 1,
                    IsValidDriver = true,
                    DimmingProtocol = Protocol,
                    Voltage = Volts,
                    DerateFactor = derate,
                    IsTbd = false
                })
                .ToList();
        }

        private static List<FixtureData> Downlights(int count, double watts)
        {
            return Enumerable.Range(0, count)
                .Select(i => new FixtureData
                {
                    FixtureId = new ElementRef(1000 + i),
                    TypeMark = "L",
                    TypePower = watts,
                    Manufacturer = Mfr,
                    DimmingProtocol = Protocol,
                    Voltage = Volts,
                    HasRemotePowerSupply = true
                })
                .ToList();
        }

        [Fact]
        public void SmallLoad_PicksSmallestTransformerThatFits_Not1000W()
        {
            // 4 × 7.5 W = 30 W — the reported case. Every type holds it in one unit, so all nine
            // tie on driver/sub-driver count; the 100 W must win, not the lexically-first 1000 W.
            var rec = new DriverSelectionService().GetRecommendation(Downlights(4, 7.5), TransformerLadder());

            Assert.True(rec.HasMatch);
            Assert.Equal("100W", rec.DriverType);
            Assert.Equal(1, rec.DriverCount);
        }

        [Theory]
        [InlineData(120, "150W")]   // just over 100 ⇒ 150
        [InlineData(100, "100W")]   // exactly 100 ⇒ 100 (inclusive fit)
        [InlineData(175, "250W")]   // between 150 and 250 ⇒ 250
        [InlineData(600, "600W")]   // exactly 600 ⇒ 600
        public void PicksSmallestRatingThatFits_NoDerate(double totalWatts, string expectedType)
        {
            // One fixture carrying the whole load, so the pick is purely "smallest cap ≥ load".
            var rec = new DriverSelectionService().GetRecommendation(Downlights(1, totalWatts), TransformerLadder());

            Assert.Equal(expectedType, rec.DriverType);
            Assert.Equal(1, rec.DriverCount);
        }

        [Fact]
        public void Derate_PushesToNextSizeUp_WhenLoadExceedsDeratedCeiling()
        {
            // 225 W at 80 %: 250×0.8 = 200 < 225 ⇒ the 250 splits into 2 units and loses on
            // DriversNeeded; 300×0.8 = 240 ≥ 225 ⇒ one unit. So the 300 W wins, not the 250 W.
            var rec = new DriverSelectionService().GetRecommendation(Downlights(1, 225), TransformerLadder(derate: 0.80));

            Assert.Equal("300W", rec.DriverType);
            Assert.Equal(1, rec.DriverCount);
        }

        [Fact]
        public void Derate_InclusiveAtDeratedCeiling()
        {
            // 200 W at 80 %: 250×0.8 = 200 ≥ 200 (inclusive) ⇒ the 250 holds it in one unit and,
            // being smaller than the 300, wins.
            var rec = new DriverSelectionService().GetRecommendation(Downlights(1, 200), TransformerLadder(derate: 0.80));

            Assert.Equal("250W", rec.DriverType);
            Assert.Equal(1, rec.DriverCount);
        }
    }
}
