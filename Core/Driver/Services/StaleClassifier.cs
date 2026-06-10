#nullable disable
using System;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Staleness state of a single RPS circuit's placed power supplies relative to a fresh
    /// <see cref="DriverRecommendation"/>. See <c>StaleClassifier</c> for the meaning of each.
    /// </summary>
    public enum RpsStatus
    {
        /// <summary>Placed type == recommended type, same physical count.</summary>
        Ok,

        /// <summary>Recommended type differs but is same family + same driver count — fixable
        /// by an in-place <c>FamilyInstance.Symbol</c> swap (the only auto-correct case).</summary>
        Stale,

        /// <summary>Needs delete+re-place (route to TurboDriver): physical count changed,
        /// recommendation is a different family (cross-family swap throws), or the placed
        /// supplies are mixed/ambiguous.</summary>
        Rebuild,

        /// <summary>Circuit has RPS fixtures but no power supplies placed yet.</summary>
        NotDeployed,

        /// <summary>No real driver fits the circuit (underspecified or no candidate matches).</summary>
        NoMatch
    }

    /// <summary>Result of classification: the status plus, for <see cref="RpsStatus.Rebuild"/>,
    /// a short human reason shown as the "→ TurboDriver" hint.</summary>
    public readonly struct RpsClassification
    {
        public RpsClassification(RpsStatus status, string rebuildReason)
        {
            Status = status;
            RebuildReason = rebuildReason;
        }

        public RpsStatus Status { get; }

        /// <summary>Non-null only for <see cref="RpsStatus.Rebuild"/>
        /// (e.g. "different family", "count 1→2", "mixed types").</summary>
        public string RebuildReason { get; }
    }

    /// <summary>
    /// Pure, Revit-free classifier driving the TurboRPS staleness dashboard. Inputs are
    /// summaries (counts + candidate identity), never live Revit objects. See
    /// <c>yes-lets-make-it-vast-starfish.md</c> for the full design and the "switch
    /// granularity" rationale: the Revit switch-system unit is the physical driver instance
    /// (= <see cref="DriverRecommendation.DriverCount"/>), so sub-driver/channel count is an
    /// internal type property and drops out of classification.
    /// </summary>
    public static class StaleClassifier
    {
        /// <param name="placedInstanceCount">Number of placed driver instances on the circuit
        /// (drivers only — non-driver lighting devices like keypads are excluded upstream).</param>
        /// <param name="distinctPlacedTypeCount">Distinct placed driver types among those instances.</param>
        /// <param name="placedCandidate">The single placed driver type's candidate info; null when
        /// none placed or the set is mixed.</param>
        /// <param name="recommendation">Fresh recommendation for the circuit's current fixtures.</param>
        public static RpsClassification Classify(
            int placedInstanceCount,
            int distinctPlacedTypeCount,
            DriverCandidateInfo placedCandidate,
            DriverRecommendation recommendation)
        {
            // Evaluate in the order documented in the plan.

            // 1. NotDeployed — qualifying circuit with zero placed supplies.
            if (placedInstanceCount == 0)
                return new RpsClassification(RpsStatus.NotDeployed, null);

            // 2. NoMatch — no real driver fits (underspecified or nothing matched).
            if (recommendation == null
                || !recommendation.HasMatch
                || recommendation.RecommendedCandidate == null)
            {
                return new RpsClassification(RpsStatus.NoMatch, null);
            }

            var reco = recommendation.RecommendedCandidate;

            // 3. Rebuild — any condition that a Symbol swap cannot satisfy.
            if (distinctPlacedTypeCount > 1)
                return new RpsClassification(RpsStatus.Rebuild, "mixed types");

            if (placedInstanceCount != recommendation.DriverCount)
            {
                return new RpsClassification(RpsStatus.Rebuild,
                    $"count {placedInstanceCount}→{recommendation.DriverCount}");
            }

            // Cross-family recommendation: FamilyInstance.Symbol assignment throws across
            // families, so it cannot be applied in place.
            if (placedCandidate == null || !FamilyEquals(placedCandidate, reco))
                return new RpsClassification(RpsStatus.Rebuild, "different family");

            // 4 & 5. Same family, same count, single placed type. Channel count is irrelevant.
            bool sameType = placedCandidate.SymbolRef == reco.SymbolRef;
            return sameType
                ? new RpsClassification(RpsStatus.Ok, null)
                : new RpsClassification(RpsStatus.Stale, null);
        }

        private static bool FamilyEquals(DriverCandidateInfo a, DriverCandidateInfo b)
            => !string.IsNullOrEmpty(a.FamilyName)
               && !string.IsNullOrEmpty(b.FamilyName)
               && string.Equals(a.FamilyName, b.FamilyName, StringComparison.OrdinalIgnoreCase);
    }
}
