using System;
using System.Collections.Generic;

namespace TurboSuite.Shared.Hosting
{
    /// <summary>
    /// Pure classifier: given the coarse <see cref="HostKind"/> and the resolved host category, decide
    /// the <see cref="HostRiskTier"/> and the one-line note shown to the user. This is the single place
    /// the tier boundaries live — retune <see cref="StableLinkedCategories"/> to change what counts as
    /// low-risk hosting. No Revit types, so it is unit-tested directly (Tests/HostRiskClassifierTests).
    /// </summary>
    public static class HostRiskClassifier
    {
        /// <summary>
        /// The linked host categories treated as "stable" — least likely to be deleted or moved out
        /// from under a hosted fixture. Everything else linked is <see cref="HostRiskTier.ChurnRisk"/>.
        /// YOUR CALL: this set is the knob. Ceilings churn in remodels too, so demote it here if that
        /// bites; casework/furniture/stairs/doors/generic-models are deliberately left OUT (churn-prone).
        /// </summary>
        private static readonly HashSet<string> StableLinkedCategories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Walls", "Ceilings", "Floors", "Roofs",
            };

        public static (HostRiskTier Tier, string Note) Classify(HostKind kind, string? hostCategory)
        {
            switch (kind)
            {
                case HostKind.Unhosted:
                    return (HostRiskTier.Unhosted,
                        "Not hosted to anything — a 2D / free placement. Expected for drafting families; "
                        + "unexpected for a fixture you meant to mount, which would sit unhosted.");

                case HostKind.LinkedUnresolved:
                    return (HostRiskTier.Orphaned,
                        "Hosted into a link, but the host element could not be resolved — its id is stale "
                        + "or invalid. The linked host was likely deleted: this element is effectively orphaned.");

                case HostKind.HostDocElement:
                    return (HostRiskTier.HostDocIntentional,
                        $"Hosted to your own {hostCategory ?? "element"} in this model — a deliberate in-model "
                        + "host (e.g. a track fixture on its track family), not the arch link.");

                case HostKind.LinkedElement:
                    if (hostCategory != null && StableLinkedCategories.Contains(hostCategory))
                        return (HostRiskTier.Stable,
                            $"Hosted to a linked {hostCategory} — a stable structural host.");

                    return (HostRiskTier.ChurnRisk,
                        $"Hosted to a linked {hostCategory ?? "element"} — more prone to churn/deletion than a "
                        + "wall. If that linked host is deleted or reworked, this element is orphaned.");

                default:
                    return (HostRiskTier.Unhosted, string.Empty);
            }
        }
    }
}
