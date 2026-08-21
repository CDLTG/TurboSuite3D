#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>
    /// Assigns <c>L{loop}-{short##}</c> addresses to DALI <b>units</b> (a driver device or a self-driven
    /// downlight — <see cref="DaliUnitReading"/>), lock-aware — the two-level analog of
    /// <c>Core/Dmx/Lock/DmxLockReconciler</c>. Where DMX's DEC# is one flat count, a DALI address is TWO
    /// numbers, so the DMX Fresh/Pinned reconcile runs <b>twice, nested</b>:
    ///
    /// <list type="bullet">
    ///   <item><b>Level 1 — the loop number (L#).</b> Anchor <c>LoopId</c> (a durable creation-time GUID, not
    ///   the display name). Fresh ⇒ contiguous <c>L1..LN</c> in declared order. Pinned ⇒ each loop keeps its
    ///   issued L#; a new loop appends past the high-water; a deleted loop <b>leaves a gap</b> (never
    ///   refilled — the DMX rule).</item>
    ///   <item><b>Level 2 — the short address (-##), within each loop.</b> Anchor the durable <b>unit key</b>
    ///   (driver: <c>circuit.UniqueId#ordinal</c>; downlight: fixture UniqueId — NOT the driver element's
    ///   UniqueId, so it survives a driver redeploy). Fresh ⇒ <b>zero-based</b> <c>00..</c> in the canonical
    ///   order: member-zone declared order (outer) → within a zone, an NW-seeded proximity walk over each
    ///   circuit's fixture centroid, and each driver-bearing circuit expands into its several driver units in
    ///   ordinal (down-column) order at that walked spot (<see cref="ProximityWalk"/>). One uniform walk — a
    ///   mixed zone needs no special block split; driver circuits simply emit N contiguous addresses where
    ///   their tape sits. Each zone is a contiguous block — the Lutron alignment. Pinned ⇒ each unit keeps its
    ///   issued slot; a new unit appends past its loop's high-water; a deleted unit's slot <b>gaps</b>.</item>
    /// </list>
    ///
    /// <para><b>Why per-zone high-water collapses to loop high-water here.</b> The append is naturally framed
    /// as "past the zone's high-water within the loop." Under the two invariants this engine enforces —
    /// numbering is contiguous at lock, and a retired slot is never reused — every number at or below a loop's
    /// high-water is already taken (by a live-frozen or a retired unit), so "smallest free number above this
    /// zone's frozen block" is ALWAYS the loop's high-water + 1. The two rules therefore produce the identical
    /// result; the zone-block tidiness is realized on the next <b>unlock + re-walk</b> (Fresh). We keep the
    /// simpler, provably freeze-safe loop-high-water append and document the equivalence here.</para>
    ///
    /// <para><b>REVIEW verdicts</b> (surfaced, never applied silently — the DMX rule). Only when locked:</para>
    /// <list type="bullet">
    ///   <item>a locked unit <b>moved loops</b> ⇒ its issued <c>L{n}-{s}</c> now names the wrong loop
    ///   (the unit is renumbered into its new loop and the change is flagged);</item>
    ///   <item>a locked unit <b>is gone</b> ⇒ its address is retired (gap, no reuse);</item>
    ///   <item>a unit <b>moving zones within the same loop</b> keeps a valid address (its L# is still right) ⇒
    ///   <b>silent</b>, mirroring DMX not flagging a same-count decoder-type swap.</item>
    /// </list>
    /// </summary>
    public static class DaliAddressReconciler
    {
        public static DaliAddressing Reconcile(
            IReadOnlyList<DaliLoopInput> loops,
            IReadOnlyList<DaliUnitReading> units,
            DaliSnapshotDto? baseline,
            bool locked)
        {
            loops ??= Array.Empty<DaliLoopInput>();
            units ??= Array.Empty<DaliUnitReading>();

            // Index the live units by zone (a zone → its units), first-wins on a duplicate unit key.
            var byZone = new Dictionary<string, List<DaliUnitReading>>(StringComparer.OrdinalIgnoreCase);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in units)
            {
                if (u.UnitKey.Length == 0 || u.Zone.Length == 0) continue;   // unaddressable
                if (!seenKeys.Add(u.UnitKey)) continue;                      // dedupe a repeated key
                if (!byZone.TryGetValue(u.Zone, out var list))
                    byZone[u.Zone] = list = new List<DaliUnitReading>();
                list.Add(u);
            }

            // Canonical per-loop unit order: member-zone declared order (outer) → within a zone, NW-seeded walk
            // over the circuits, each circuit expanded into its units in ordinal order (inner).
            // A zone belongs to one loop (single membership, enforced upstream + defensively here).
            var claimedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loopOrder = new List<(DaliLoopInput Loop, List<string> UnitKeys)>();
            var loopByUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // unitKey → loopId

            foreach (var loop in loops)
            {
                var keys = new List<string>();
                foreach (var zone in loop.ZoneNames)
                {
                    if (!claimedZones.Add(zone)) continue;                   // zone already in an earlier loop
                    if (!byZone.TryGetValue(zone, out var zoneUnits)) continue;

                    foreach (string unitKey in OrderZone(zoneUnits))
                    {
                        keys.Add(unitKey);
                        loopByUnit[unitKey] = loop.LoopId;
                    }
                }
                loopOrder.Add((loop, keys));
            }

            bool pinned = locked && baseline != null &&
                          (baseline.Loops.Count > 0 || baseline.Units.Count > 0);

            var loopNumbers = pinned
                ? PinnedLoopNumbers(loops, baseline!)
                : FreshLoopNumbers(loops);

            var slotByUnit = pinned
                ? PinnedLoadSlots(loopOrder, baseline!)
                : FreshLoadSlots(loopOrder);

            // Compose addresses in canonical order.
            var addresses = new List<DaliUnitAddress>();
            var unitZone = units
                .Where(u => u.UnitKey.Length > 0)
                .GroupBy(u => u.UnitKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Zone, StringComparer.OrdinalIgnoreCase);

            foreach (var (loop, keys) in loopOrder)
            {
                if (!loopNumbers.TryGetValue(loop.LoopId, out int loopNum)) continue;
                foreach (string key in keys)
                {
                    if (!slotByUnit.TryGetValue(key, out int slot)) continue;
                    string zone = unitZone.TryGetValue(key, out var z) ? z : "";
                    addresses.Add(new DaliUnitAddress(key, zone, loop.LoopId,
                        new DaliAddress(loopNum, slot)));
                }
            }

            // Every unit still in the model (any zone, even blank) — to tell a deleted unit from one whose
            // loop/zone was merely unassigned (both retire the address, but the copy differs).
            var presentKeys = new HashSet<string>(
                units.Where(u => u.UnitKey.Length > 0).Select(u => u.UnitKey),
                StringComparer.OrdinalIgnoreCase);

            var reviews = pinned
                ? BuildReviews(baseline!, loopByUnit, presentKeys)
                : new List<DaliReviewItem>();

            return new DaliAddressing(addresses, loopNumbers, reviews);
        }

        // ── Inner ordering: walk the circuits, expand each into its units ────────────────────────────────

        /// <summary>Order a zone's units: NW-seeded proximity walk over the distinct circuits (by their shared
        /// fixture centroid), then within each circuit its units in ordinal (down-column) order. A single-unit
        /// (downlight) circuit contributes one; a driver circuit contributes its N drivers contiguously.</summary>
        private static List<string> OrderZone(List<DaliUnitReading> zoneUnits)
        {
            var byCircuit = new Dictionary<string, List<DaliUnitReading>>(StringComparer.Ordinal);
            var circuitOrder = new List<string>();
            foreach (var u in zoneUnits)
            {
                if (!byCircuit.TryGetValue(u.CircuitKey, out var list))
                {
                    byCircuit[u.CircuitKey] = list = new List<DaliUnitReading>();
                    circuitOrder.Add(u.CircuitKey);
                }
                list.Add(u);
            }

            var nodes = circuitOrder
                .Select(ck => new WalkNode(ck, byCircuit[ck][0].Centroid))
                .ToList();

            var result = new List<string>();
            foreach (string circuitKey in ProximityWalk.NwSeededOrder(nodes))
                foreach (var u in byCircuit[circuitKey]
                             .OrderBy(u => u.Ordinal)
                             .ThenBy(u => u.UnitKey, StringComparer.Ordinal))
                    result.Add(u.UnitKey);
            return result;
        }

        // ── Level 1: loop numbers ───────────────────────────────────────────────────────────────────────

        private static Dictionary<string, int> FreshLoopNumbers(IReadOnlyList<DaliLoopInput> loops)
        {
            var map = new Dictionary<string, int>();
            int n = 1;
            foreach (var loop in loops)
                if (!map.ContainsKey(loop.LoopId)) map[loop.LoopId] = n++;
            return map;
        }

        private static Dictionary<string, int> PinnedLoopNumbers(
            IReadOnlyList<DaliLoopInput> loops, DaliSnapshotDto baseline)
        {
            var baseNum = new Dictionary<string, int>();
            foreach (var l in baseline.Loops) baseNum[l.LoopId] = l.LoopNumber;

            // Every issued L# is reserved forever — a deleted loop's number gaps, a new loop never refills it.
            var used = new HashSet<int>(baseNum.Values);
            var map = new Dictionary<string, int>();

            foreach (var loop in loops)                                     // kept loops keep their L#
                if (baseNum.TryGetValue(loop.LoopId, out int ln))
                    map[loop.LoopId] = ln;

            int next = (used.Count == 0 ? 0 : used.Max()) + 1;
            foreach (var loop in loops)                                     // new loops append past high-water
            {
                if (map.ContainsKey(loop.LoopId)) continue;
                while (used.Contains(next)) next++;
                map[loop.LoopId] = next;
                used.Add(next);
                next++;
            }
            return map;
        }

        // ── Level 2: load slots (within each loop) — zero-based short addresses ────────────────────────────

        private static Dictionary<string, int> FreshLoadSlots(
            List<(DaliLoopInput Loop, List<string> UnitKeys)> loopOrder)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, keys) in loopOrder)
            {
                int slot = 0;                                              // zero-based: first unit is -00
                foreach (string key in keys) map[key] = slot++;
            }
            return map;
        }

        private static Dictionary<string, int> PinnedLoadSlots(
            List<(DaliLoopInput Loop, List<string> UnitKeys)> loopOrder, DaliSnapshotDto baseline)
        {
            // Baseline slot per unit, and the set of slots each loop ever issued (for gap-safe append).
            var baseSlot = new Dictionary<string, DaliSnapshotUnitDto>(StringComparer.OrdinalIgnoreCase);
            var issuedByLoop = new Dictionary<string, HashSet<int>>();
            foreach (var u in baseline.Units)
            {
                baseSlot[u.UnitKey] = u;
                if (!issuedByLoop.TryGetValue(u.LoopId, out var set))
                    issuedByLoop[u.LoopId] = set = new HashSet<int>();
                set.Add(u.LoadNumber);
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (loop, keys) in loopOrder)
            {
                issuedByLoop.TryGetValue(loop.LoopId, out var issued);
                var used = new HashSet<int>(issued ?? Enumerable.Empty<int>());
                int next = used.Count == 0 ? 0 : used.Max() + 1;          // zero-based next-free

                // Kept-in-this-loop units reuse their slot; new/moved-in units append past high-water,
                // in canonical order so the append is deterministic.
                var appendees = new List<string>();
                foreach (string key in keys)
                {
                    if (baseSlot.TryGetValue(key, out var b) &&
                        string.Equals(b.LoopId, loop.LoopId, StringComparison.OrdinalIgnoreCase))
                        map[key] = b.LoadNumber;                             // pinned in place
                    else
                        appendees.Add(key);                                 // brand-new OR moved in from elsewhere
                }

                foreach (string key in appendees)
                {
                    while (used.Contains(next)) next++;                     // skip issued (live + retired) — no reuse
                    map[key] = next;
                    used.Add(next);
                    next++;
                }
            }
            return map;
        }

        // ── REVIEW verdicts ─────────────────────────────────────────────────────────────────────────────

        private static List<DaliReviewItem> BuildReviews(
            DaliSnapshotDto baseline, Dictionary<string, string> loopByUnit, HashSet<string> presentKeys)
        {
            var reviews = new List<DaliReviewItem>();
            foreach (var b in baseline.Units)
            {
                string issued = new DaliAddress(b.LoopNumber, b.LoadNumber).Text;
                string zoneLabel = string.IsNullOrWhiteSpace(b.Zone) ? "" : $" ({b.Zone})";

                if (loopByUnit.TryGetValue(b.UnitKey, out string? currentLoopId))
                {
                    if (!string.Equals(currentLoopId, b.LoopId, StringComparison.OrdinalIgnoreCase))
                        // Moved loops — its L# is now wrong; renumbered into the new loop, flagged.
                        reviews.Add(new DaliReviewItem(b.UnitKey,
                            $"{issued}{zoneLabel} — unit moved to a different loop since lock; "
                            + "its issued address named the old loop. Re-issued on its new loop."));
                    // else same loop (even if the zone changed within it) ⇒ silent: the L# is still correct.
                }
                else if (presentKeys.Contains(b.UnitKey))
                {
                    // Still in the model, but its loop was removed / zone unassigned ⇒ no longer addressed.
                    reviews.Add(new DaliReviewItem(b.UnitKey,
                        $"{issued}{zoneLabel} — no longer on an addressed loop; address retired. "
                        + "Unlock to reclaim the number."));
                }
                else
                {
                    // Gone from the model — deleted. No reuse; unlock to reclaim.
                    reviews.Add(new DaliReviewItem(b.UnitKey,
                        $"{issued}{zoneLabel} — unit deleted; address retired. Unlock to reclaim the number."));
                }
            }
            return reviews;
        }
    }
}
