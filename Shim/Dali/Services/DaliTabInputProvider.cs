#nullable disable
using System;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using TurboSuite.Zones.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliTabInputProvider"/> — collects the DALI tab's inputs from the model (plan
    /// H4: TurboDALI reads its own inputs, no dependency on TurboZones' persisted state). Used at window open
    /// AND on Refresh, so both paths read the model identically: DALI loads-by-zone for the pool, the
    /// model-derived panel-ZONE list, and the persisted loops.
    /// </summary>
    public sealed class DaliTabInputProvider : IDaliTabInputProvider
    {
        private readonly Document _doc;

        public DaliTabInputProvider(Document doc) => _doc = doc;

        public DaliTabInputs Read()
        {
            var loadsByZone = DaliDemandProvider.CountDaliLoadsByZone(_doc, out _);
            var zones = loadsByZone
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new DaliZoneItemViewModel(kv.Key, kv.Value))
                .ToList();

            var circuits = new ZonesCollectorService().GetCircuits(_doc);
            var panelZones = PanelAllocationService.DiscoverPanelZones(circuits);

            var saved = DaliStorageService.Load(_doc);

            return new DaliTabInputs(zones, panelZones, saved);
        }
    }
}
