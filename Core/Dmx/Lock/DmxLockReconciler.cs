#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx.Lock
{
    /// <summary>
    /// Assigns DEC numbers to a solved system, lock-aware. The lock baseline is
    /// **Control-Zone-anchored** (decision 2026-06-26): a locked re-run pins each zone to the DEC #s it was
    /// issued at Lock, so already-issued numbers never move.
    ///
    /// • Unlocked ⇒ fresh deterministic numbering: walk zones in canonical order, hand out 1..N. (Most of a
    ///   project lives here — "always re-stamp" is trivially correct because nothing is committed.)
    /// • Locked ⇒ pin to the baseline:
    ///   - A zone in the baseline keeps its issued DEC #s for the decoders it still has (slot i ⇒ baseline #i).
    ///   - **Additive** decoders (a zone grew, or a brand-new zone) append fresh numbers past the baseline's
    ///     high-water mark — never refilling a retired gap, so a number never lands on a different box than
    ///     issued. Existing-zone extras are numbered before any new-zone decoders, so adding a zone later
    ///     never shifts an already-appended number.
    ///   - **Retired** decoders (a zone shrank / a zone removed) just drop their numbers — gaps, no renumber.
    ///   - A zone whose **interface #** changed still keeps its slot numbers, but its issued numbering now
    ///     lives on a different loop ⇒ surfaced as **REVIEW** (never silent). A **decoder-type** change is
    ///     NOT flagged (decision 2026-06-30): numbers are pinned by slot, so a same-count swap moves no
    ///     number/address; the model/BOM delta is TurboDocs/Counts' job, not the numbering lock's.
    /// </summary>
    public static class DmxLockReconciler
    {
        public static DmxNumbering Reconcile(IReadOnlyList<DmxSolvedZone> zones, DmxSnapshotDto? baseline, bool locked)
        {
            if (!locked || baseline == null || baseline.Zones.Count == 0)
                return Fresh(zones);

            return Pinned(zones, baseline);
        }

        // Unlocked: contiguous 1..N in canonical order.
        private static DmxNumbering Fresh(IReadOnlyList<DmxSolvedZone> zones)
        {
            var result = new List<DmxZoneNumbering>(zones.Count);
            int next = 1;
            foreach (var z in zones)
            {
                var ids = new List<int>(z.DecoderCount);
                for (int i = 0; i < z.DecoderCount; i++) ids.Add(next++);
                result.Add(new DmxZoneNumbering(z.ZoneValue, z.InterfaceNumber, z.DecoderType, ids));
            }
            return new DmxNumbering(result, new List<DmxReviewItem>());
        }

        // Locked: pin baseline zones, append additive numbers, flag type/interface drift.
        private static DmxNumbering Pinned(IReadOnlyList<DmxSolvedZone> zones, DmxSnapshotDto baseline)
        {
            var baseByZone = new Dictionary<string, DmxSnapshotZoneDto>();
            foreach (var b in baseline.Zones) baseByZone[b.ZoneValue] = b;

            int highWater = baseline.Zones.SelectMany(z => z.DecIds).DefaultIfEmpty(0).Max();
            int nextAppend = highWater + 1;

            // Pre-size the per-zone id lists so we can fill across two passes while returning canonical order.
            var ids = zones.ToDictionary(z => z.ZoneValue, z => new int[z.DecoderCount]);
            var reviews = new List<DmxReviewItem>();

            // Pass 1 — baseline zones (canonical order among them): reuse issued #s, append this zone's extras.
            foreach (var z in zones.Where(z => baseByZone.ContainsKey(z.ZoneValue)))
            {
                var b = baseByZone[z.ZoneValue];
                var slots = ids[z.ZoneValue];
                for (int i = 0; i < z.DecoderCount; i++)
                    slots[i] = i < b.DecIds.Count ? b.DecIds[i] : nextAppend++;

                // A decoder-TYPE change is deliberately NOT a REVIEW (decision 2026-06-30): the DEC #s are
                // pinned by slot, so a same-count type swap moves no number and shifts no address — only the
                // physical decoder model differs, and that BOM/part-number change is already surfaced by
                // TurboDocs/Counts. We only flag the one drift that actually relabels issued numbering:
                if (z.InterfaceNumber != b.InterfaceNumber)
                    reviews.Add(new DmxReviewItem(z.ZoneValue,
                        $"Zone \"{z.ZoneValue}\": reassigned to interface #{z.InterfaceNumber} (was #{b.InterfaceNumber}) "
                        + "since lock; its issued DEC #s now sit on a different DMX loop."));
            }

            // Pass 2 — new zones (not in baseline): append entirely after all baseline-zone extras.
            foreach (var z in zones.Where(z => !baseByZone.ContainsKey(z.ZoneValue)))
            {
                var slots = ids[z.ZoneValue];
                for (int i = 0; i < z.DecoderCount; i++) slots[i] = nextAppend++;
            }

            // Emit in canonical order.
            var result = zones
                .Select(z => new DmxZoneNumbering(z.ZoneValue, z.InterfaceNumber, z.DecoderType, ids[z.ZoneValue]))
                .ToList();
            return new DmxNumbering(result, reviews);
        }
    }
}
