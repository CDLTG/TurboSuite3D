#nullable enable
using System.Collections.Generic;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.ViewModels;

namespace TurboSuite.Dali.Services
{
    /// <summary>The three inputs the DALI declaration UI is built (and re-built, on Refresh) from: the
    /// Control-Zone pool with load counts, the model-derived panel-ZONE list for the assign dropdown, and the
    /// persisted loop declarations. Collected shim-side by <see cref="IDaliTabInputProvider"/> so both the
    /// open-time build and a live Refresh read the model exactly the same way.</summary>
    public sealed class DaliTabInputs
    {
        public DaliTabInputs(
            IReadOnlyList<DaliZoneItemViewModel> zones,
            IReadOnlyList<int> panelZones,
            DaliModuleState saved)
        {
            Zones = zones;
            PanelZones = panelZones;
            Saved = saved;
        }

        public IReadOnlyList<DaliZoneItemViewModel> Zones { get; }
        public IReadOnlyList<int> PanelZones { get; }
        public DaliModuleState Saved { get; }
    }
}
