#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// The orphan-location bookkeeping behind the one user input in Section 1 (plan item 5): which pool a
    /// processor-less-but-panel-bearing location joins. Pure and Revit-free, so the reconciliation rule is
    /// pinned by tests; the ViewModel only reads the allocation and persists the map.
    ///
    /// <b>An orphan</b> is a location with located panels (dimmer or shade) but no processor of its own —
    /// its panels must still wire to <i>some</i> processor's QS link. <b>A host</b> is any
    /// processor-bearing location (one pool, even with two processors). The designer assigns an orphan to a
    /// host; the assignment relabels the orphan's units onto the host's location before the pack
    /// (<see cref="ControlLinkPacker.RelabelLocations"/>), so the packer only ever sees "prefer a matching
    /// location". Location 0 (a name that carries no location) is never an orphan or a host — such units
    /// float location-lessly.
    /// </summary>
    public static class OrphanLocationService
    {
        private const string ProcessorDevice = "Processor";

        /// <summary>Location numbers that hold ≥1 processor — the pools an orphan can join.</summary>
        public static SortedSet<int> HostLocations(PanelAllocationResult? allocation)
        {
            var hosts = new SortedSet<int>();
            if (allocation == null) return hosts;

            foreach (var loc in allocation.Locations)
            {
                if (loc.LocationNumber <= 0) continue;
                if (loc.Panels.Any(HasProcessor))
                    hosts.Add(loc.LocationNumber);
            }
            return hosts;
        }

        /// <summary>Location numbers with located panels (dimmer or shade) but no processor — every one is
        /// offered a pool assignment, shade-only locations included.</summary>
        public static SortedSet<int> OrphanLocations(PanelAllocationResult? allocation)
        {
            var orphans = new SortedSet<int>();
            if (allocation == null) return orphans;

            foreach (var loc in allocation.Locations)
            {
                if (loc.LocationNumber <= 0) continue;
                bool hasPanels = loc.Panels.Count > 0 || loc.ShadePanels.Count > 0;
                if (hasPanels && !loc.Panels.Any(HasProcessor))
                    orphans.Add(loc.LocationNumber);
            }
            return orphans;
        }

        /// <summary>
        /// Discards stale assignments — the pure re-derivation the DMX/DALI discard-on-load pattern uses.
        /// An entry survives only when its <b>key is still an orphan</b> and its <b>value is still a
        /// host</b>; that single filter covers every staleness path (the orphan got its own processor; the
        /// host lost its; a rename removed a location). No chains: a host has a processor and an orphan does
        /// not, so a surviving value is never itself a surviving key. A self-map (orphan → itself) can never
        /// survive — a location cannot be both — so it is dropped too.
        /// </summary>
        public static Dictionary<int, int> Reconcile(
            IReadOnlyDictionary<int, int>? map, PanelAllocationResult? allocation)
        {
            var result = new Dictionary<int, int>();
            if (map == null || map.Count == 0) return result;

            var orphans = OrphanLocations(allocation);
            var hosts = HostLocations(allocation);

            foreach (var kvp in map)
            {
                if (kvp.Key != kvp.Value && orphans.Contains(kvp.Key) && hosts.Contains(kvp.Value))
                    result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        private static bool HasProcessor(PanelResult panel)
            => panel.CompartmentSlots.Any(s =>
                string.Equals(s, ProcessorDevice, StringComparison.OrdinalIgnoreCase));
    }
}
