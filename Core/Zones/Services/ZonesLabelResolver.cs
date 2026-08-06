using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Pure load-name label resolution, extracted from the Revit-coupled
    /// ZonesCollectorService so Core consumers (e.g. ZonesCircuitViewModel) can use
    /// it without a Revit reference.
    /// </summary>
    public static class ZonesLabelResolver
    {
        /// <summary>
        /// Resolves the label portion of a load name from pre-read values.
        /// Priority: circuit comments > fixture comments > load classification name.
        /// Parenthetical content is stripped from the two comment paths only — the
        /// load-classification fallback is read straight (see below).
        /// </summary>
        public static string ResolveLabel(string circuitComments, string fixtureComments, string loadClassificationName, out LabelSource source)
        {
            // Priority 1: Circuit Comments
            if (!string.IsNullOrWhiteSpace(circuitComments))
            {
                source = LabelSource.CircuitComments;
                return StripParenthetical(circuitComments);
            }

            // Priority 2: Fixture Comments (unique, joined)
            if (!string.IsNullOrWhiteSpace(fixtureComments))
            {
                source = LabelSource.FixtureComments;
                return StripParenthetical(fixtureComments);
            }

            // Priority 3: Load Classification (full name), read straight.
            // The stripping this used to do existed to cut "DOWNLIGHTS (ELV)" down to
            // "DOWNLIGHTS" — back when the classification was being repurposed to smuggle the
            // module type. TurboSuite no longer reads it for that (see DimmingModuleResolver),
            // so the value is a plain classification name and sanitizing it would only
            // corrupt a legitimate parenthetical.
            if (!string.IsNullOrWhiteSpace(loadClassificationName))
            {
                source = LabelSource.Fallback;
                return loadClassificationName;
            }

            source = LabelSource.None;
            return string.Empty;
        }

        private static string StripParenthetical(string label)
        {
            int parenIdx = label.IndexOf('(');
            if (parenIdx >= 0)
                label = label.Substring(0, parenIdx).TrimEnd();
            return label ?? string.Empty;
        }
    }
}
