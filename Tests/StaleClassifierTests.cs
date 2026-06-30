using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using Xunit;

namespace TurboSuite.Tests.Driver
{
    /// <summary>
    /// Oracle for <see cref="StaleClassifier"/> — focuses on the TurboRPS-2 DMX-decoder branch: a
    /// channelized circuit driven by a wired decoder must classify as <see cref="RpsStatus.DmxManaged"/>
    /// ("present &amp; wired → not driver-managed"), NOT mis-flag as NotDeployed just because no wattage
    /// driver is placed. Also re-asserts the pre-existing branches stay intact when the DMX flag is off.
    ///
    /// CONFIDENCE: Tier A. The classifier is pure; every assertion is a direct branch verdict.
    /// </summary>
    public class StaleClassifierTests
    {
        private static DriverCandidateInfo Cand(string family = "AL_RPS_60", string symbol = "60W")
            => new DriverCandidateInfo
            {
                FamilyName = family,
                FamilyTypeName = symbol,
                SymbolRef = new ElementRef(symbol.GetHashCode()),
                IsValidDriver = true
            };

        private static DriverRecommendation Match(DriverCandidateInfo reco, int count)
            => new DriverRecommendation
            {
                HasMatch = true,
                RecommendedCandidate = reco,
                DriverCount = count
            };

        // ── TurboRPS-2: DMX-decoder circuits ─────────────────────────────────────────────────────────

        [Fact]
        public void DmxDecoderManaged_TakesPrecedence_OverNotDeployed()
        {
            // No driver placed (placedCount 0) and no recommendation — the old code returned NotDeployed.
            var result = StaleClassifier.Classify(
                placedInstanceCount: 0,
                distinctPlacedTypeCount: 0,
                placedCandidate: null,
                recommendation: null,
                isDmxDecoderManaged: true);

            Assert.Equal(RpsStatus.DmxManaged, result.Status);
        }

        [Fact]
        public void DmxFlagOff_StillNotDeployed_WhenNothingPlaced()
        {
            var result = StaleClassifier.Classify(0, 0, null, null, isDmxDecoderManaged: false);
            Assert.Equal(RpsStatus.NotDeployed, result.Status);
        }

        // ── Regression: the existing branches are unchanged with the DMX flag defaulted off ──────────

        [Fact]
        public void NoMatch_WhenPlacedButNoRecommendation()
        {
            var result = StaleClassifier.Classify(1, 1, Cand(), recommendation: null);
            Assert.Equal(RpsStatus.NoMatch, result.Status);
        }

        [Fact]
        public void Ok_WhenPlacedTypeMatchesRecommendation()
        {
            var cand = Cand();
            var result = StaleClassifier.Classify(1, 1, cand, Match(cand, 1));
            Assert.Equal(RpsStatus.Ok, result.Status);
        }

        [Fact]
        public void Stale_WhenSameFamilyDifferentType_SameCount()
        {
            var placed = Cand(symbol: "60W");
            var reco = Cand(symbol: "90W");
            var result = StaleClassifier.Classify(1, 1, placed, Match(reco, 1));
            Assert.Equal(RpsStatus.Stale, result.Status);
        }

        [Fact]
        public void Rebuild_WhenCountDiffers()
        {
            var cand = Cand();
            var result = StaleClassifier.Classify(1, 1, cand, Match(cand, 2));
            Assert.Equal(RpsStatus.Rebuild, result.Status);
        }
    }
}
