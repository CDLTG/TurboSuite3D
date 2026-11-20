#nullable disable
using System;
using System.Linq;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Helpers for the TurboRPS "defer" feature. A deferral records the config the user knowingly
    /// accepted; <see cref="Signature"/> distills that config into a compact string so a later scan
    /// can tell whether the circuit has drifted since it was deferred (placed/recommended/verdict
    /// changed) and should be re-surfaced for review.
    /// </summary>
    public static class RpsDeferral
    {
        /// <summary>Compact signature of the verdict + placed + recommended config. Stored at defer
        /// time and recomputed on each scan; a mismatch means "config changed — review".</summary>
        public static string Signature(RpsCircuitData data)
        {
            if (data == null) return string.Empty;
            string signature = string.Join("|",
                data.Status,
                data.PlacedTypeName ?? string.Empty,
                data.PlacedCount,
                data.DistinctPlacedTypeCount,
                data.RecommendedTypeName ?? string.Empty,
                data.RecommendedCount);

            // A tie also records WHICH families tie, so a third family joining (or one dropping out)
            // re-surfaces the deferral. Appended only for Tie, so every non-tie signature stays
            // byte-identical to the stored ones and existing deferrals don't flip to REVIEW.
            if (data.Status == RpsStatus.Tie && data.TiedCandidates != null && data.TiedCandidates.Count > 0)
            {
                var families = data.TiedCandidates
                    .Select(c => c.FamilyName ?? string.Empty)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                signature += "|tie:" + string.Join(",", families);
            }

            return signature;
        }
    }
}
