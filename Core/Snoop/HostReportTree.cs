using TurboSuite.Shared.Hosting;

namespace TurboSuite.Snoop.Models
{
    /// <summary>
    /// Renders a <see cref="HostResolution"/> into the same <see cref="SnoopNode"/> tree the VG-checkbox
    /// report uses, so the single-element host report reuses the TurboSnoop window verbatim. Pure — no
    /// Revit types — so it is unit-tested (Tests/HostReportTreeTests). Every row is <see cref="SnoopNodeKind.Info"/>:
    /// these are informational key/value lines, not VG checkboxes, so none get the leaf bullet.
    ///
    /// The picked element's own label is NOT put in the tree — it goes in the window header (like the VG
    /// report's family label), via <see cref="HostResolution.PickedLabel"/>.
    /// </summary>
    public static class HostReportTree
    {
        public static SnoopNode Build(HostResolution res)
        {
            // Root headline summarises the tier; children carry the specifics.
            var root = new SnoopNode(Headline(res), SnoopNodeKind.Family);

            if (res.HostLabel != null)
                root.Children.Add(new SnoopNode($"Host element: {res.HostLabel}", SnoopNodeKind.Info));

            if (res.HostCategory != null)
                root.Children.Add(new SnoopNode($"Host category: {res.HostCategory}", SnoopNodeKind.Info));

            if (res.LinkName != null)
                root.Children.Add(new SnoopNode($"In link: {res.LinkName}", SnoopNodeKind.Info));

            root.Children.Add(new SnoopNode(res.Note, SnoopNodeKind.Info));

            return root;
        }

        private static string Headline(HostResolution res)
        {
            switch (res.Tier)
            {
                case HostRiskTier.Unhosted:
                    return "Not hosted (2D / free placement)";
                case HostRiskTier.HostDocIntentional:
                    return $"Hosted to your own {res.HostCategory ?? "element"} (intentional)";
                case HostRiskTier.Stable:
                    return $"Hosted to {res.HostCategory} (stable)";
                case HostRiskTier.ChurnRisk:
                    return $"Hosted to {res.HostCategory ?? "a linked element"} — churn risk";
                case HostRiskTier.Orphaned:
                    return "Host could not be resolved — possibly orphaned";
                default:
                    return "Host";
            }
        }
    }
}
