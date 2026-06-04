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
        /// Strips parenthetical content from the result.
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

            // Priority 3: Load Classification (full name)
            if (!string.IsNullOrWhiteSpace(loadClassificationName))
            {
                source = LabelSource.Fallback;
                return StripParenthetical(loadClassificationName);
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
