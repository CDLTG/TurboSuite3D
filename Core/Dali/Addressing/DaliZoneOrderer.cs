#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>One circuit as the within-zone orderer sees it: the durable <see cref="CircuitKey"/>, the
    /// circuit's resolved <see cref="Room"/> (for the Auto sort's room rank), and its shared walk
    /// <see cref="Centroid"/>. Built identically by the two callers — <see cref="DaliAddressReconciler"/>
    /// (from the zone's <c>DaliUnitReading</c>s) and the DALI tab (from the seed projection) — so the order a
    /// designer SEES and the order that gets WRITTEN are the same function of the same input.</summary>
    public readonly struct ZoneCircuit
    {
        public ZoneCircuit(string circuitKey, string? room, DaliPoint? centroid)
        {
            CircuitKey = circuitKey ?? "";
            Room = (room ?? "").Trim();
            Centroid = centroid;
        }

        public string CircuitKey { get; }
        public string Room { get; }
        public DaliPoint? Centroid { get; }
    }

    /// <summary>Per-zone sort state: Auto (the default — room-order then spatial) or Manual (a designer-dragged
    /// circuit order, frozen until reset). <see cref="CircuitOrder"/> is meaningful only when
    /// <see cref="Manual"/>; an Auto zone persists nothing and this reads back as the default.</summary>
    public sealed class DaliZoneSort
    {
        public static readonly DaliZoneSort Auto = new DaliZoneSort(false, Array.Empty<string>());

        public DaliZoneSort(bool manual, IReadOnlyList<string>? circuitOrder)
        {
            Manual = manual;
            CircuitOrder = circuitOrder ?? Array.Empty<string>();
        }

        public bool Manual { get; }
        public IReadOnlyList<string> CircuitOrder { get; }
    }

    /// <summary>
    /// The single within-zone circuit-ordering function, shared by the reconciler and the tab's displayed
    /// order so they cannot disagree (mirrors how <c>ControlLinkPacker</c> is the single link computation).
    ///
    /// <list type="bullet">
    ///   <item><b>Manual</b> — emit circuits in the persisted order; any circuit not in that saved list
    ///   (newly added since the drag) is appended at the end in Auto order, so it never disappears.</item>
    ///   <item><b>Auto</b> — group the circuits by <b>room RANK</b> (not room name), run the NW-seeded
    ///   proximity walk within each rank group, concatenate the groups in ascending rank order. Every unranked
    ///   room (absent from the order, or blank) shares rank <see cref="int.MaxValue"/>, so they fall into one
    ///   trailing group walked spatially among themselves. The consequence that matters: an <b>empty room
    ///   order ⇒ every circuit is one rank ⇒ a single NW walk over all circuits = the pre-feature
    ///   behavior, byte-for-byte</b>. (Grouping by room NAME would wrongly split a no-room-order job into
    ///   per-room walks.)</item>
    /// </list>
    /// </summary>
    public static class DaliZoneOrderer
    {
        public static IReadOnlyList<string> Order(
            IReadOnlyList<ZoneCircuit> zoneCircuits,
            IReadOnlyDictionary<string, int> roomRank,
            DaliZoneSort? zoneSort)
        {
            if (zoneCircuits == null || zoneCircuits.Count == 0) return Array.Empty<string>();

            // De-dupe to distinct circuits in first-seen order (the caller may pass one entry per unit).
            var byKey = new Dictionary<string, ZoneCircuit>(StringComparer.Ordinal);
            var seen = new List<string>();
            foreach (var zc in zoneCircuits)
            {
                if (byKey.ContainsKey(zc.CircuitKey)) continue;
                byKey[zc.CircuitKey] = zc;
                seen.Add(zc.CircuitKey);
            }

            List<string> auto = AutoOrder(seen, byKey, roomRank);

            if (zoneSort == null || !zoneSort.Manual) return auto;

            // Manual: persisted order first (only keys that still exist), then any not-yet-placed circuit in
            // Auto order so a newly added circuit is appended rather than dropped.
            var result = new List<string>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in zoneSort.CircuitOrder)
                if (byKey.ContainsKey(key) && placed.Add(key))
                    result.Add(key);
            foreach (string key in auto)
                if (placed.Add(key))
                    result.Add(key);
            return result;
        }

        private static List<string> AutoOrder(
            List<string> keys,
            Dictionary<string, ZoneCircuit> byKey,
            IReadOnlyDictionary<string, int>? roomRank)
        {
            int Rank(string key)
            {
                string room = byKey[key].Room;
                if (room.Length > 0 && roomRank != null && roomRank.TryGetValue(room, out int r)) return r;
                return int.MaxValue;
            }

            // Stable group by rank (GroupBy preserves first-seen order of both groups and members), ordered by
            // rank ascending; within each group an NW-seeded spatial walk over the circuits' centroids.
            var result = new List<string>();
            foreach (var group in keys.GroupBy(Rank).OrderBy(g => g.Key))
            {
                var nodes = group
                    .Select(k => new WalkNode(k, byKey[k].Centroid))
                    .ToList();
                result.AddRange(ProximityWalk.NwSeededOrder(nodes));
            }
            return result;
        }
    }
}
