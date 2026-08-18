namespace TurboSuite.Shared.Hosting
{
    /// <summary>
    /// What an element is hosted to, at the coarse grain the report branches on. The Revit-facing
    /// walk (<c>Shim/Shared/Services/HostResolutionService</c>) produces exactly one of these per
    /// element; <see cref="HostRiskClassifier"/> maps it (plus the host category) to a risk tier.
    /// </summary>
    public enum HostKind
    {
        /// <summary>No host at all — a 2D/free (unhosted) placement. Not a problem, just reported.</summary>
        Unhosted,

        /// <summary>Hosted to another element in THIS model (e.g. a track fixture on its track family).
        /// Deliberate in-model hosting, resolved directly from <c>Host</c>.</summary>
        HostDocElement,

        /// <summary>Hosted to an element in a loaded link, and that element resolved
        /// (<c>HostFace.LinkedElementId</c> → a live linked element).</summary>
        LinkedElement,

        /// <summary>Hosted to a link, but the specific host element could NOT be resolved in it —
        /// the id is stale/invalid or the face carries none. The likely-orphaned case.</summary>
        LinkedUnresolved,
    }

    /// <summary>
    /// How exposed an element is to losing its host out from under it. Ordered loosely by concern.
    /// The tier boundaries are a judgement call — see <see cref="HostRiskClassifier"/>, which is the
    /// single place to retune them.
    /// </summary>
    public enum HostRiskTier
    {
        /// <summary>Unhosted — informational, no host to lose.</summary>
        Unhosted,

        /// <summary>Linked structural host (walls/ceilings/floors/roofs) — least likely to churn.</summary>
        Stable,

        /// <summary>Hosted to your own in-model element (track etc.) — intentional, informational.</summary>
        HostDocIntentional,

        /// <summary>Linked non-structural host (casework, furniture, generic models, stairs, doors …) —
        /// more prone to deletion/churn; deleting the host orphans this element.</summary>
        ChurnRisk,

        /// <summary>Link-hosted but the host element does not resolve — possibly already orphaned.</summary>
        Orphaned,
    }

    /// <summary>
    /// The resolved host story for one element — pure data, no Revit types, so it drives both the
    /// single-element TurboSnoop report and (later) the full-model audit, and can be unit-tested.
    /// </summary>
    public sealed class HostResolution
    {
        public HostResolution(
            HostKind kind,
            HostRiskTier tier,
            string pickedLabel,
            string? pickedCategory,
            string? hostLabel,
            string? hostCategory,
            string? linkName,
            string note)
        {
            Kind = kind;
            Tier = tier;
            PickedLabel = pickedLabel;
            PickedCategory = pickedCategory;
            HostLabel = hostLabel;
            HostCategory = hostCategory;
            LinkName = linkName;
            Note = note;
        }

        public HostKind Kind { get; }

        public HostRiskTier Tier { get; }

        /// <summary>The reported element's own "FamilyName : Type (id)" label.</summary>
        public string PickedLabel { get; }

        /// <summary>The reported element's own category (e.g. "Lighting Devices").</summary>
        public string? PickedCategory { get; }

        /// <summary>The resolved host's "FamilyName : Type (id)" (or type-only) label; null when unhosted
        /// or unresolved.</summary>
        public string? HostLabel { get; }

        /// <summary>The resolved host's category (e.g. "Casework"); null when unhosted or unresolved.</summary>
        public string? HostCategory { get; }

        /// <summary>The host link's instance name; null for in-model or unhosted hosts.</summary>
        public string? LinkName { get; }

        /// <summary>One-line plain-language explanation of the tier, safe to show verbatim.</summary>
        public string Note { get; }
    }
}
