#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Dali.Input
{
    /// <summary>
    /// The persisted DALI loops → panel-placement mapping. Turns each declared loop's required
    /// ZONE N assignment into the <c>zone → DaliPanelModule[]</c> map that
    /// <see cref="TurboSuite.Zones.Services.PanelAllocationService.BuildPanelBreakdown"/> consumes, plus the
    /// set of loops that carry loads but were never assigned a zone (the "not placed" warning).
    ///
    /// <b>Placement, never order.</b> The module count and QS-link budget are the job-wide
    /// <see cref="TurboSuite.Dali.Services"/>/DaliSolver authority; this only decides <i>which panel</i> a
    /// module occupies a slot in. It shares <see cref="DaliStateMapper.Reconcile"/> with the demand path, so
    /// the placed loops and the ordered loops are the same reconciled set — an assigned loop is placed, an
    /// unassigned loop is ordered-but-warned, and neither can drift from the other (one computation, two questions).
    ///
    /// A reconciled loop with <b>zero loads</b> (its zones carry no DALI fixtures) is dropped from both the
    /// map and the warning: it orders no module (matching <c>DaliSolver</c>), so there is nothing to place
    /// and nothing to warn about.
    /// </summary>
    public static class DaliPlacementMapper
    {
        private static readonly IReadOnlyDictionary<string, int> EmptyLoads =
            new Dictionary<string, int>();

        public static DaliPlacement Build(
            IEnumerable<Persistence.DaliLoopDto>? loops,
            IReadOnlyDictionary<string, int>? loadsByZone)
        {
            var loads = loadsByZone ?? EmptyLoads;
            var byZone = new Dictionary<int, List<DaliPanelModule>>();
            var unassigned = new List<DaliPanelModule>();

            foreach (var loop in DaliStateMapper.Reconcile(loops, loads.Keys))
            {
                int loadCount = loop.Zones.Sum(z => loads.TryGetValue(z, out int n) ? n : 0);
                if (loadCount == 0) continue;   // orders no module ⇒ nothing to place or warn

                var module = new DaliPanelModule(loop.Dto.Name, loadCount);
                int zone = loop.Dto.AssignedZone;
                if (zone <= 0)
                {
                    unassigned.Add(module);     // ordered by the job-wide demand, but has no panel to sit in
                    continue;
                }

                if (!byZone.TryGetValue(zone, out var list))
                    byZone[zone] = list = new List<DaliPanelModule>();
                list.Add(module);
            }

            return new DaliPlacement(byZone, unassigned);
        }
    }

    /// <summary>The DALI placement result: the allocator's <c>zone → modules</c> map, and the loops that
    /// have loads but no zone (ordered job-wide, not placed — the tab warns).</summary>
    public sealed class DaliPlacement
    {
        internal DaliPlacement(
            IReadOnlyDictionary<int, List<DaliPanelModule>> byZone,
            IReadOnlyList<DaliPanelModule> unassigned)
        {
            ByZone = byZone.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<DaliPanelModule>)kv.Value);
            Unassigned = unassigned;
        }

        /// <summary>Placed modules keyed by the ZONE N the designer assigned their loop to — the map
        /// <see cref="TurboSuite.Zones.Services.PanelAllocationService.BuildPanelBreakdown"/> takes.</summary>
        public IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> ByZone { get; }

        /// <summary>Loops with loads but no ZONE N — still ordered, never placed. Non-empty ⇒ the tab shows
        /// "N DALI loops unassigned — modules not placed".</summary>
        public IReadOnlyList<DaliPanelModule> Unassigned { get; }
    }
}
