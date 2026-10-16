#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliTabInputProvider"/> — collects TurboDALI's loop-declaration inputs from the
    /// model (TurboDALI reads its own inputs, no dependency on TurboZones' persisted state). Used at window
    /// open AND on Refresh, so both paths read the model identically: DALI loads-by-zone for the pool, the
    /// model-derived panel-ZONE list, and the persisted loops.
    /// </summary>
    public sealed class DaliTabInputProvider : IDaliTabInputProvider
    {
        private readonly Document _doc;

        public DaliTabInputProvider(Document doc) => _doc = doc;

        public DaliTabInputs Read()
        {
            var loadsByZone = DaliDemandProvider.CountDaliLoadsByZone(_doc, out _);
            var seedsByZone = BuildCircuitSeedsByZone();

            var zones = loadsByZone
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new DaliZoneItemViewModel(
                    kv.Key, kv.Value,
                    seedsByZone.TryGetValue(kv.Key, out var seeds) ? seeds : null))
                .ToList();

            var circuits = new ZonesCollectorService().GetCircuits(_doc);
            var panelLocations = PanelAllocationService.DiscoverPanelLocations(circuits);

            var saved = DaliStorageService.Load(_doc);

            // The project-wide room order (TurboNumber's shared primitive) — the Auto within-zone sort's outer
            // key. Read-only consumer; empty when the job has none.
            var roomOrder = RoomOrderStorageService.Load(_doc).Select(r => r.Name).ToList();

            return new DaliTabInputs(zones, panelLocations, saved, roomOrder);
        }

        /// <summary>Project the circuited unit readings into one <see cref="DaliCircuitSeed"/> per distinct
        /// circuit, grouped by Control Zone — the per-zone sort input. Rides the same
        /// <see cref="DaliUnitEnumerator"/> walk as the counts, so the pool and the sort can't disagree.</summary>
        private Dictionary<string, IReadOnlyList<DaliCircuitSeed>> BuildCircuitSeedsByZone()
        {
            var byZone = new Dictionary<string, List<DaliCircuitSeed>>(StringComparer.OrdinalIgnoreCase);
            var seenCircuitInZone = new HashSet<string>(StringComparer.Ordinal);

            foreach (var r in DaliUnitEnumerator.Enumerate(_doc))
            {
                if (r.Zone.Length == 0 || r.CircuitKey.Length == 0) continue;   // unzoned / uncircuited

                // One row per distinct circuit within a zone (the reorder grain), from its first unit.
                string dedupeKey = r.Zone + "\u0000" + r.CircuitKey;
                if (!seenCircuitInZone.Add(dedupeKey)) continue;

                if (!byZone.TryGetValue(r.Zone, out var list))
                    byZone[r.Zone] = list = new List<DaliCircuitSeed>();
                list.Add(new DaliCircuitSeed(r.CircuitKey, BuildLabel(r), r.Room, r.Centroid));
            }

            return byZone.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DaliCircuitSeed>)kv.Value,
                                       StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The draggable row label: driver → <c>SwitchId(base) — LoadName</c> (whichever parts exist);
        /// downlight → LoadName, falling back to Room, then a neutral placeholder. Cosmetic only.</summary>
        private static string BuildLabel(DaliUnitReading r)
        {
            if (r.Kind == DaliUnitKind.Driver && r.SwitchId.Length > 0)
                return r.LoadName.Length > 0 ? $"{r.SwitchId} — {r.LoadName}" : r.SwitchId;

            if (r.LoadName.Length > 0) return r.LoadName;
            if (r.Room.Length > 0) return r.Room;
            return "(circuit)";
        }
    }
}
