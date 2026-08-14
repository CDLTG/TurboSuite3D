#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>
    /// Assigns <c>L{loop}-{load##}</c> addresses to DALI circuits, lock-aware — the two-level analog of
    /// <c>Core/Dmx/Lock/DmxLockReconciler</c>. Where DMX's DEC# is one flat count, a DALI address is TWO
    /// numbers, so the DMX Fresh/Pinned reconcile runs <b>twice, nested</b>:
    ///
    /// <list type="bullet">
    ///   <item><b>Level 1 — the loop number (L#).</b> Anchor <c>LoopId</c> (a durable creation-time GUID, not
    ///   the display name). Fresh ⇒ contiguous <c>L1..LN</c> in declared order. Pinned ⇒ each loop keeps its
    ///   issued L#; a new loop appends past the high-water; a deleted loop <b>leaves a gap</b> (never
    ///   refilled — the DMX rule).</item>
    ///   <item><b>Level 2 — the load number (-##), within each loop.</b> Anchor <c>circuit.UniqueId</c>.
    ///   Fresh ⇒ <c>01..</c> in the <b>canonical order</b>: member-zone declared order (outer) → NW-seeded
    ///   proximity walk within the zone (inner) (<see cref="ProximityWalk"/>). Each zone is a contiguous
    ///   block — the Lutron alignment. Pinned ⇒ each circuit keeps its issued slot; a new circuit appends
    ///   past its loop's high-water; a deleted circuit's slot <b>gaps</b> (no reuse — a retired address is
    ///   never re-offered while locked).</item>
    /// </list>
    ///
    /// <para><b>Why per-zone high-water collapses to loop high-water here.</b> The load append is naturally
    /// framed as "past the zone's high-water within the loop." Under the two invariants this engine enforces —
    /// numbering is contiguous at lock, and a retired slot is never reused — every number at or below a loop's
    /// high-water is already taken (by a live-frozen or a retired circuit), so "smallest free number above
    /// this zone's frozen block" is ALWAYS the loop's high-water + 1. The two rules therefore produce the
    /// identical result; the zone-block tidiness is realized on the next <b>unlock + re-walk</b>
    /// (Fresh), exactly as the within-loop-move rule anticipates. We keep the code as the simpler,
    /// provably freeze-safe loop-high-water append and document the equivalence here rather than build a
    /// per-zone scheme with no observable effect.</para>
    ///
    /// <para><b>REVIEW verdicts</b> (surfaced, never applied silently — the DMX rule). Only when locked:</para>
    /// <list type="bullet">
    ///   <item>a locked circuit <b>moved loops</b> ⇒ its issued <c>L{n}-{s}</c> now names the wrong loop
    ///   (the circuit is renumbered into its new loop and the change is flagged);</item>
    ///   <item>a locked circuit <b>is gone</b> ⇒ its address is retired (gap, no reuse).</item>
    ///   <item>a circuit <b>moving zones within the same loop</b> keeps a valid address (its L# is still
    ///   right; the tag is just out of its tidy block until an unlock+re-walk) ⇒ <b>silent</b>, mirroring DMX
    ///   not flagging a same-count decoder-type swap.</item>
    /// </list>
    /// </summary>
    public static class DaliAddressReconciler
    {
        public static DaliAddressing Reconcile(
            IReadOnlyList<DaliLoopInput> loops,
            IReadOnlyList<DaliCircuitReading> circuits,
            DaliSnapshotDto? baseline,
            bool locked)
        {
            loops ??= Array.Empty<DaliLoopInput>();
            circuits ??= Array.Empty<DaliCircuitReading>();

            // Index the live circuits by zone (a zone → its circuits), first-wins on a duplicate key.
            var byZone = new Dictionary<string, List<DaliCircuitReading>>(StringComparer.OrdinalIgnoreCase);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in circuits)
            {
                if (c.CircuitKey.Length == 0 || c.Zone.Length == 0) continue;   // unaddressable
                if (!seenKeys.Add(c.CircuitKey)) continue;                      // dedupe a repeated key
                if (!byZone.TryGetValue(c.Zone, out var list))
                    byZone[c.Zone] = list = new List<DaliCircuitReading>();
                list.Add(c);
            }

            // Canonical per-loop circuit order: member-zone declared order (outer) → NW-seeded walk (inner).
            // A zone belongs to one loop (single membership, enforced upstream + defensively here).
            var claimedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loopOrder = new List<(DaliLoopInput Loop, List<string> CircuitKeys)>();
            var loopByCircuit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // key → loopId

            foreach (var loop in loops)
            {
                var keys = new List<string>();
                foreach (var zone in loop.ZoneNames)
                {
                    if (!claimedZones.Add(zone)) continue;                      // zone already in an earlier loop
                    if (!byZone.TryGetValue(zone, out var zoneCircuits)) continue;

                    var nodes = zoneCircuits
                        .Select(c => new WalkNode(c.CircuitKey, c.Centroid))
                        .ToList();
                    foreach (string key in ProximityWalk.NwSeededOrder(nodes))
                    {
                        keys.Add(key);
                        loopByCircuit[key] = loop.LoopId;
                    }
                }
                loopOrder.Add((loop, keys));
            }

            bool pinned = locked && baseline != null &&
                          (baseline.Loops.Count > 0 || baseline.Circuits.Count > 0);

            var loopNumbers = pinned
                ? PinnedLoopNumbers(loops, baseline!)
                : FreshLoopNumbers(loops);

            var slotByCircuit = pinned
                ? PinnedLoadSlots(loopOrder, baseline!)
                : FreshLoadSlots(loopOrder);

            // Compose addresses in canonical order.
            var addresses = new List<DaliCircuitAddress>();
            var circuitZone = circuits
                .Where(c => c.CircuitKey.Length > 0)
                .GroupBy(c => c.CircuitKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Zone, StringComparer.OrdinalIgnoreCase);

            foreach (var (loop, keys) in loopOrder)
            {
                if (!loopNumbers.TryGetValue(loop.LoopId, out int loopNum)) continue;
                foreach (string key in keys)
                {
                    if (!slotByCircuit.TryGetValue(key, out int slot)) continue;
                    string zone = circuitZone.TryGetValue(key, out var z) ? z : "";
                    addresses.Add(new DaliCircuitAddress(key, zone, loop.LoopId,
                        new DaliAddress(loopNum, slot)));
                }
            }

            // Every circuit still in the model (any zone, even blank) — to tell a deleted circuit from one
            // whose loop/zone was merely unassigned (both retire the address, but the copy differs).
            var presentKeys = new HashSet<string>(
                circuits.Where(c => c.CircuitKey.Length > 0).Select(c => c.CircuitKey),
                StringComparer.OrdinalIgnoreCase);

            var reviews = pinned
                ? BuildReviews(baseline!, loopByCircuit, presentKeys)
                : new List<DaliReviewItem>();

            return new DaliAddressing(addresses, loopNumbers, reviews);
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

        // ── Level 2: load slots (within each loop) ──────────────────────────────────────────────────────

        private static Dictionary<string, int> FreshLoadSlots(
            List<(DaliLoopInput Loop, List<string> CircuitKeys)> loopOrder)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, keys) in loopOrder)
            {
                int slot = 1;
                foreach (string key in keys) map[key] = slot++;
            }
            return map;
        }

        private static Dictionary<string, int> PinnedLoadSlots(
            List<(DaliLoopInput Loop, List<string> CircuitKeys)> loopOrder, DaliSnapshotDto baseline)
        {
            // Baseline slot per circuit, and the set of slots each loop ever issued (for gap-safe append).
            var baseSlot = new Dictionary<string, DaliSnapshotCircuitDto>(StringComparer.OrdinalIgnoreCase);
            var issuedByLoop = new Dictionary<string, HashSet<int>>();
            foreach (var c in baseline.Circuits)
            {
                baseSlot[c.CircuitKey] = c;
                if (!issuedByLoop.TryGetValue(c.LoopId, out var set))
                    issuedByLoop[c.LoopId] = set = new HashSet<int>();
                set.Add(c.LoadNumber);
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (loop, keys) in loopOrder)
            {
                issuedByLoop.TryGetValue(loop.LoopId, out var issued);
                var used = new HashSet<int>(issued ?? Enumerable.Empty<int>());
                int highWater = used.Count == 0 ? 0 : used.Max();
                int next = highWater + 1;

                // Kept-in-this-loop circuits reuse their slot; new/moved-in circuits append past high-water,
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
            DaliSnapshotDto baseline, Dictionary<string, string> loopByCircuit, HashSet<string> presentKeys)
        {
            var reviews = new List<DaliReviewItem>();
            foreach (var b in baseline.Circuits)
            {
                string issued = new DaliAddress(b.LoopNumber, b.LoadNumber).Text;
                string zoneLabel = string.IsNullOrWhiteSpace(b.Zone) ? "" : $" ({b.Zone})";

                if (loopByCircuit.TryGetValue(b.CircuitKey, out string? currentLoopId))
                {
                    if (!string.Equals(currentLoopId, b.LoopId, StringComparison.OrdinalIgnoreCase))
                        // Moved loops — its L# is now wrong; renumbered into the new loop, flagged.
                        reviews.Add(new DaliReviewItem(b.CircuitKey,
                            $"{issued}{zoneLabel} — circuit moved to a different loop since lock; "
                            + "its issued address named the old loop. Re-issued on its new loop."));
                    // else same loop (even if the zone changed within it) ⇒ silent: the L# is still correct.
                }
                else if (presentKeys.Contains(b.CircuitKey))
                {
                    // Still in the model, but its loop was removed / zone unassigned ⇒ no longer addressed.
                    reviews.Add(new DaliReviewItem(b.CircuitKey,
                        $"{issued}{zoneLabel} — no longer on an addressed loop; address retired. "
                        + "Unlock to reclaim the number."));
                }
                else
                {
                    // Gone from the model — deleted. No reuse; unlock to reclaim.
                    reviews.Add(new DaliReviewItem(b.CircuitKey,
                        $"{issued}{zoneLabel} — circuit deleted; address retired. Unlock to reclaim the number."));
                }
            }
            return reviews;
        }
    }
}
