#nullable enable
using System.Collections.Generic;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.ViewModels;

namespace TurboSuite.Dali.Services
{
    /// <summary>The three inputs the DALI declaration UI is built (and re-built, on Refresh) from: the
    /// Control-Zone pool with load counts, the model-derived panel-location list for the assign dropdown, and
    /// the persisted loop declarations. Collected shim-side by <see cref="IDaliTabInputProvider"/> so both the
    /// open-time build and a live Refresh read the model exactly the same way.</summary>
    public sealed class DaliTabInputs
    {
        public DaliTabInputs(
            IReadOnlyList<DaliZoneItemViewModel> zones,
            IReadOnlyList<int> panelLocations,
            DaliModuleState saved,
            IReadOnlyList<string>? roomOrder = null)
        {
            Zones = zones;
            PanelLocations = panelLocations;
            Saved = saved;
            RoomOrder = roomOrder ?? System.Array.Empty<string>();
        }

        /// <summary>The Control-Zone pool, each VM pre-seeded with its circuit sort (<c>DaliCircuitSeed</c>s)
        /// so the tab can order + display circuits without a second model read.</summary>
        public IReadOnlyList<DaliZoneItemViewModel> Zones { get; }
        public IReadOnlyList<int> PanelLocations { get; }
        public DaliModuleState Saved { get; }

        /// <summary>The project-wide room order (TurboNumber's <c>RoomOrderStorageService</c>), the Auto sort's
        /// outer key. Empty when the job has none ⇒ pure spatial, unchanged from the pre-feature behavior.</summary>
        public IReadOnlyList<string> RoomOrder { get; }
    }
}
