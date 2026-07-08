#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx.Input
{
    /// <summary>
    /// Coalesces individual fixture readings into <b>bundles</b> — the atomic packable unit — before the
    /// decoder packer sees them. Some products (e.g. a 1'×2' LED light sheet) can only be field-connected
    /// in daisy-chains of up to N; the packer must never slice such a chain at a single-fixture boundary,
    /// so a whole chain is aggregated into ONE <see cref="TapeRun"/> (summed watts + length). The existing
    /// "never split a drawn run" contract in <c>DecoderPacker.PackToCap</c> then enforces chain integrity
    /// for free.
    ///
    /// The unit is a <b>max, not a divisor</b>: 72 fixtures @ max 5 ⇒ 14 chains of 5 + 1 chain of 2 = 15
    /// bundles (remainder chains are legal). Fixtures with <c>MaxPerBundle ≤ 1</c> slice at size 1 — one
    /// run per fixture = the pre-bundle behavior (backward-safe default).
    ///
    /// Pure / Revit-free. Used by BOTH the solve path (<see cref="DmxZoneBuilder"/>) and the read-only
    /// row display, so the "→ N bundles" count always matches the count actually packed.
    /// </summary>
    public static class DmxBundler
    {
        /// <summary>A fixture as the bundler sees it: its id (for deterministic ordering), its per-fixture
        /// <see cref="TapeRun"/>, the max fixtures per chain, and the product key (Type Mark) it may only
        /// chain within.</summary>
        public readonly struct Item
        {
            public Item(long id, TapeRun run, int maxPerBundle, string productKey)
            {
                Id = id;
                Run = run;
                MaxPerBundle = maxPerBundle;
                ProductKey = productKey ?? "";
            }

            public long Id { get; }
            public TapeRun Run { get; }
            public int MaxPerBundle { get; }
            public string ProductKey { get; }
        }

        /// <summary>Group by (product, channels, max) → sort by id → slice into chains of ≤ max → sum each
        /// chain into one <see cref="TapeRun"/>. Groups are emitted in first-seen order and, within a
        /// group, in id order, so the result is stable (the numbering lock / one-line never thrash on a
        /// re-solve).</summary>
        public static IReadOnlyList<TapeRun> Bundle(IEnumerable<Item> fixtures)
        {
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));

            var result = new List<TapeRun>();
            foreach (var group in GroupInFirstSeenOrder(fixtures))
            {
                int max = Math.Max(1, group[0].MaxPerBundle);
                var ordered = group.OrderBy(i => i.Id).ToList();   // stable: ties keep input order
                for (int start = 0; start < ordered.Count; start += max)
                    result.Add(Coalesce(ordered.Skip(start).Take(max)));
            }
            return result;
        }

        /// <summary>The bundle count only, without building the runs — for the read-only row annotation.
        /// Equivalent to <c>Bundle(fixtures).Count</c> but allocation-free.</summary>
        public static int CountBundles(IEnumerable<Item> fixtures)
        {
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));

            int total = 0;
            foreach (var group in GroupInFirstSeenOrder(fixtures))
            {
                int max = Math.Max(1, group[0].MaxPerBundle);
                total += (group.Count + max - 1) / max;   // ceil(count / max)
            }
            return total;
        }

        // Group by (product key, channels, max) preserving the order each group's first member appeared.
        private static List<List<Item>> GroupInFirstSeenOrder(IEnumerable<Item> fixtures)
        {
            var order = new List<(string Key, int Ch, int Max)>();
            var groups = new Dictionary<(string, int, int), List<Item>>();
            foreach (var f in fixtures)
            {
                var key = (f.ProductKey, f.Run.Channels, Math.Max(1, f.MaxPerBundle));
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Item>();
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add(f);
            }
            return order.Select(k => groups[k]).ToList();
        }

        // Aggregate a chain into one run: total length, total-watts-÷-total-length W/ft, shared channels.
        // Preserves both total watts and total length exactly (robust to mixed wattages within a product).
        private static TapeRun Coalesce(IEnumerable<Item> chain)
        {
            double lengthFt = 0, watts = 0;
            int channels = 0;
            foreach (var i in chain)
            {
                lengthFt += i.Run.LengthFt;
                watts += PowerMath.TotalWatts(i.Run);
                channels = i.Run.Channels;   // uniform within a group (channels are part of the key)
            }
            double wattsPerFt = lengthFt > 1e-9 ? watts / lengthFt : 0.0;
            return new TapeRun(lengthFt, wattsPerFt, channels);
        }
    }
}
