using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Grammar: <c>{&lt;format&gt;[,&lt;option&gt;]}</c>.
/// Formats: <c>xx</c> → <c>48</c> (unitless inches); <c>xx"</c> → <c>48"</c>; <c>xxIN</c> → <c>48IN</c>;
/// <c>ft</c> → <c>4</c> (unitless feet); <c>xx'</c> → <c>4'</c>; <c>xxFT</c> → <c>4FT</c>;
/// <c>xx'-xx"</c> → <c>4'-0"</c>; <c>xxFT-xxIN</c> → <c>4FT-0IN</c>.
/// Feet formats truncate (integer divide by 12).
/// Options:
///   <c>max=N</c> — made-to-length: greedy-splits each instance into N-sized cuts plus one remainder.
///   <c>sizes=N1|N2|...</c> — discrete stock sizes (e.g. 96|48): covers each instance with the
///     fewest sticks whose sum ≥ instance length, tie-breaking on least overage.
///   <c>pool=N1|N2|...</c> — same stock sizes as sizes=, but reuses offcuts across instances.
///     Use when offcuts are physically fungible with fresh-stick cut ends (raw aluminum channel,
///     raw track without factory-finished ends). Hardcoded 18" minimum reusable offcut.
///   <c>min=N</c> — floor on the emitted cut length: any piece below N (a short made-to-length
///     remainder, or a whole instance shorter than N) is clamped UP to N, over-supplying rather
///     than shipping a sub-minimum cut. A modifier, not a mode — it combines with max= or a bare
///     token, and is the one thing that lets the max/plain paths strand material.
/// max=, sizes=, and pool= are mutually exclusive; min= combines only with max= or a bare token
/// (not sizes=/pool=, whose stock lengths already clear any sane floor). Unknown formats / option
/// keys / non-integer values / min &gt; max / illegal option combinations fail validation.
/// </summary>
public static class CatalogLengthTokenResolver
{
    // {xx}, {xx"}, {xxIN}, {ft}, {xx'}, {xxFT}, {xx'-xx"}, {xxFT-xxIN} with optional comma-separated options.
    private static readonly Regex TokenRegex = new(
        @"\{(?<fmt>xx'-xx""|xxFT-xxIN|xx""|xxIN|xx'|xxFT|xx|ft)(?:,(?<opts>[^}]*))?\}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFormats = new(StringComparer.Ordinal)
    {
        "xx",
        "xx\"",
        "xxIN",
        "ft",
        "xx'",
        "xxFT",
        "xx'-xx\"",
        "xxFT-xxIN",
    };

    public sealed record ParsedToken(string Format, int? MaxInches, string Raw, int Index, int Length);

    public static bool HasToken(string? catalogNumber)
    {
        if (string.IsNullOrEmpty(catalogNumber)) return false;
        // net48's string.IsNullOrEmpty lacks [NotNullWhen(false)], so flag non-null explicitly.
        return (catalogNumber!.Contains("{xx", StringComparison.Ordinal)
            || catalogNumber!.Contains("{ft", StringComparison.Ordinal))
            && TokenRegex.IsMatch(catalogNumber);
    }

    /// <summary>
    /// Validates every length token in <paramref name="catalogNumber"/>.
    /// Throws <see cref="CatalogLengthTokenParseException"/> on the first failure.
    /// </summary>
    public static void Validate(string? catalogNumber)
    {
        if (string.IsNullOrEmpty(catalogNumber)) return;

        int cursor = 0;
        while (true)
        {
            int iXx = catalogNumber!.IndexOf("{xx", cursor, StringComparison.Ordinal);
            int iFt = catalogNumber.IndexOf("{ft", cursor, StringComparison.Ordinal);
            int idx;
            if (iXx >= 0 && (iFt < 0 || iXx <= iFt)) idx = iXx;
            else if (iFt >= 0) idx = iFt;
            else break;
            var m = TokenRegex.Match(catalogNumber, idx);
            if (!m.Success || m.Index != idx)
                throw new CatalogLengthTokenParseException(catalogNumber, "Malformed length token (expected {xx}, {xx\"}, {xxIN}, {ft}, {xx'}, {xxFT}, {xx'-xx\"}, {xxFT-xxIN}, optionally with ',max=N' / ',sizes=N|N|...' / ',pool=N|N|...' / ',min=N')");
            ValidateToken(catalogNumber, m);
            cursor = m.Index + m.Length;
        }
    }

    private static void ValidateToken(string raw, Match m)
    {
        string fmt = m.Groups["fmt"].Value;
        if (!KnownFormats.Contains(fmt))
            throw new CatalogLengthTokenParseException(raw, $"Unknown length format '{fmt}' (supported: xx, xx\", xxIN, ft, xx', xxFT, xx'-xx\", xxFT-xxIN)");

        // Whole-foot formats render only whole feet, so every explicit length they carry (max, min,
        // sizes/pool stock) must land on a foot boundary — otherwise the SKU would misstate what's
        // ordered. Cuts round UP to a foot at resolve time; these length knobs are author-supplied
        // and must be exact.
        bool isWholeFoot = WholeFootFormats.Contains(fmt);

        if (!m.Groups["opts"].Success) return;

        string opts = m.Groups["opts"].Value;
        bool sawMax = false, sawSizes = false, sawPool = false, sawMin = false;
        int maxVal = 0, minVal = 0;
        foreach (var part in opts.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                throw new CatalogLengthTokenParseException(raw, $"Malformed option '{part}' (expected 'max=<int>' or 'sizes=N|N|...' or 'pool=N|N|...' or 'min=<int>')");
            string key = kv[0].Trim();
            string val = kv[1].Trim();

            if (string.Equals(key, "max", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    throw new CatalogLengthTokenParseException(raw, $"max='{val}' must be a bare positive integer (inches)");
                if (n <= 0)
                    throw new CatalogLengthTokenParseException(raw, "max must be greater than zero");
                if (isWholeFoot && n % 12 != 0)
                    throw new CatalogLengthTokenParseException(raw, $"max ({n}) must be a whole number of feet (a multiple of 12) for a '{fmt}' format");
                sawMax = true;
                maxVal = n;
            }
            else if (string.Equals(key, "min", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    throw new CatalogLengthTokenParseException(raw, $"min='{val}' must be a bare positive integer (inches)");
                if (n <= 0)
                    throw new CatalogLengthTokenParseException(raw, "min must be greater than zero");
                if (isWholeFoot && n % 12 != 0)
                    throw new CatalogLengthTokenParseException(raw, $"min ({n}) must be a whole number of feet (a multiple of 12) for a '{fmt}' format");
                sawMin = true;
                minVal = n;
            }
            else if (string.Equals(key, "sizes", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(key, "pool", StringComparison.OrdinalIgnoreCase))
            {
                bool isPool = string.Equals(key, "pool", StringComparison.OrdinalIgnoreCase);
                string kind = isPool ? "pool" : "sizes";
                if (string.IsNullOrWhiteSpace(val))
                    throw new CatalogLengthTokenParseException(raw, $"{kind} must list at least one positive integer (e.g. {kind}=96|48)");
                var seen = new HashSet<int>();
                foreach (var s in val.Split('|'))
                {
                    if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn))
                        throw new CatalogLengthTokenParseException(raw, $"{kind} entry '{s.Trim()}' must be a positive integer (inches)");
                    if (sn <= 0)
                        throw new CatalogLengthTokenParseException(raw, $"{kind} entries must be greater than zero");
                    if (isWholeFoot && sn % 12 != 0)
                        throw new CatalogLengthTokenParseException(raw, $"{kind} entry '{sn}' must be a whole number of feet (a multiple of 12) for a '{fmt}' format");
                    if (!seen.Add(sn))
                        throw new CatalogLengthTokenParseException(raw, $"{kind} entry '{sn}' is duplicated");
                }
                if (isPool) sawPool = true; else sawSizes = true;
            }
            else
            {
                throw new CatalogLengthTokenParseException(raw, $"Unknown option '{key}' (supported: max, sizes, pool, min)");
            }
        }

        int modes = (sawMax ? 1 : 0) + (sawSizes ? 1 : 0) + (sawPool ? 1 : 0);
        if (modes > 1)
            throw new CatalogLengthTokenParseException(raw, "max, sizes, and pool are mutually exclusive on the same token");

        if (sawMin && (sawSizes || sawPool))
            throw new CatalogLengthTokenParseException(raw, "min combines only with max= or a bare token, not sizes=/pool=");
        if (sawMin && sawMax && minVal > maxVal)
            throw new CatalogLengthTokenParseException(raw, $"min ({minVal}) cannot exceed max ({maxVal})");
    }

    /// <summary>
    /// Reads the first token's max= value, or null if uncapped. Caller must have already
    /// validated the template (this method assumes well-formed input on the token path).
    /// </summary>
    public static int? ParseMaxInches(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var m = TokenRegex.Match(template);
        if (!m.Success || !m.Groups["opts"].Success) return null;
        foreach (var part in m.Groups["opts"].Value.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2
                && string.Equals(kv[0].Trim(), "max", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n > 0)
                return n;
        }
        return null;
    }

    /// <summary>
    /// Reads the first token's min= value, or null if no floor. Caller must have already
    /// validated the template (this method assumes well-formed input on the token path).
    /// </summary>
    public static int? ParseMinInches(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var m = TokenRegex.Match(template);
        if (!m.Success || !m.Groups["opts"].Success) return null;
        foreach (var part in m.Groups["opts"].Value.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2
                && string.Equals(kv[0].Trim(), "min", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n > 0)
                return n;
        }
        return null;
    }

    /// <summary>
    /// Reads the first token's sizes= list (descending), or null if absent. Caller must have
    /// already validated the template.
    /// </summary>
    public static IReadOnlyList<int>? ParseSizes(string? template) => ParseSizeList(template, "sizes");

    /// <summary>
    /// Reads the first token's pool= list (descending), or null if absent. Caller must have
    /// already validated the template.
    /// </summary>
    public static IReadOnlyList<int>? ParsePool(string? template) => ParseSizeList(template, "pool");

    private static IReadOnlyList<int>? ParseSizeList(string? template, string keyName)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var m = TokenRegex.Match(template);
        if (!m.Success || !m.Groups["opts"].Success) return null;
        foreach (var part in m.Groups["opts"].Value.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (!string.Equals(kv[0].Trim(), keyName, StringComparison.OrdinalIgnoreCase)) continue;
            var list = new List<int>();
            foreach (var s in kv[1].Split('|'))
            {
                if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn) && sn > 0)
                    list.Add(sn);
            }
            if (list.Count == 0) return null;
            list.Sort((a, b) => b.CompareTo(a));
            return list;
        }
        return null;
    }

    /// <summary>
    /// Replaces every length token in <paramref name="template"/> with the rendered
    /// length for <paramref name="inches"/>. All token instances share the same length —
    /// the cut size, not the original instance size.
    /// </summary>
    public static string Resolve(string template, int inches)
    {
        return TokenRegex.Replace(template, m =>
        {
            string fmt = m.Groups["fmt"].Value;
            return Render(fmt, inches);
        });
    }

    private static string Render(string format, int inches)
    {
        switch (format)
        {
            case "xx":
                return inches.ToString(CultureInfo.InvariantCulture);
            case "xx\"":
                return $"{inches}\"";
            case "xxIN":
                return $"{inches}IN";
            case "ft":
                return (inches / 12).ToString(CultureInfo.InvariantCulture);
            case "xx'":
                return $"{inches / 12}'";
            case "xxFT":
                return $"{inches / 12}FT";
            case "xx'-xx\"":
            {
                int feet = inches / 12;
                int rem = inches % 12;
                return $"{feet}'-{rem}\"";
            }
            case "xxFT-xxIN":
            {
                int feet = inches / 12;
                int rem = inches % 12;
                return $"{feet}FT-{rem}IN";
            }
            default:
                return inches.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Greedy fill-max split. L=120, max=91 → [91, 29]; L=91, max=91 → [91];
    /// L=120, max=null → [120]. When <paramref name="minInches"/> is set, any emitted piece below
    /// it — the trailing remainder, or a whole under-min instance — is clamped UP to the floor
    /// (over-supplying rather than shipping a sub-minimum cut). Full max-sized pieces are never
    /// below the floor because validation rejects min &gt; max. L=200, max=197, min=12 → [197, 12];
    /// L=8, min=12 → [12].
    /// <para>
    /// <paramref name="granularity"/> is the orderable increment of the render format (12" for the
    /// whole-foot formats, 1" otherwise — see <see cref="GranularityInches"/>). The made-to-length
    /// pieces (trailing remainder, whole under-max instance) round UP to it, because a whole-foot SKU
    /// can only be ordered in whole feet and ordering short isn't an option — L=30, gran=12 → [36];
    /// L=8, gran=12 → [12] (never a 0' cut). Full max-sized sticks are NOT rounded: validation forces
    /// max to be a granularity multiple for the foot formats, so they already sit on the increment.
    /// </para>
    /// </summary>
    public static IEnumerable<int> SplitInstance(int instanceInches, int? maxInches, int? minInches = null, int granularity = 1)
    {
        if (instanceInches <= 0) yield break;
        int floor = minInches.GetValueOrDefault(0);
        if (!maxInches.HasValue || instanceInches <= maxInches.Value)
        {
            yield return RoundUp(Math.Max(instanceInches, floor), granularity);
            yield break;
        }
        int n = maxInches.Value;
        int full = instanceInches / n;
        int rem = instanceInches % n;
        for (int i = 0; i < full; i++) yield return n;
        if (rem > 0) yield return RoundUp(Math.Max(rem, floor), granularity);
    }

    // Round UP to the next multiple of granularity (a no-op for the 1" inch formats).
    private static int RoundUp(int value, int granularity)
        => granularity <= 1 ? value : ((value + granularity - 1) / granularity) * granularity;

    private static readonly HashSet<string> WholeFootFormats = new(StringComparer.Ordinal)
    {
        "ft", "xx'", "xxFT",
    };

    /// <summary>
    /// The orderable increment of a template's render format, in inches: 12 for the whole-foot
    /// formats (<c>ft</c>, <c>xx'</c>, <c>xxFT</c>), which can only carry whole feet, else 1. Cuts
    /// round UP to this so a feet SKU never ships a sub-foot (or 0') length. Reads the first token;
    /// callers must have validated the template.
    /// </summary>
    public static int GranularityInches(string? template)
    {
        if (string.IsNullOrEmpty(template)) return 1;
        var m = TokenRegex.Match(template);
        if (!m.Success) return 1;
        return WholeFootFormats.Contains(m.Groups["fmt"].Value) ? 12 : 1;
    }

    /// <summary>
    /// Cover an instance length with discrete stock sticks. Priority:
    ///   (1) zero-waste exact fit if reachable;
    ///   (2) else fewest pieces among covering sums (s ≥ L);
    ///   (3) tie-break on least overage (smallest s).
    /// L=192, sizes=[94,48] → [48,48,48,48] (exact, beats 1×48 + 2×94 = 236 even though that's 3 pcs).
    /// L=264, sizes=[94,48] → [94,94,94] (3 pcs, +24); 264 unreachable exactly.
    /// L=200, sizes=[94,48] → [94,48,48,48]? No — 1×48+2×94 = 236 (3 pcs, +36) beats 5×48 = 240 (5 pcs).
    /// </summary>
    public static IEnumerable<int> CoverInstance(int instanceInches, IReadOnlyList<int> sizes)
    {
        if (instanceInches <= 0 || sizes.Count == 0) yield break;

        // Unbounded-knapsack DP over [0, L + maxSize]: best[s] = min pieces to reach EXACTLY s
        // using sizes (multiset). Then apply the three-tier priority below.
        int maxSize = sizes[0]; // ParseSizes sorts desc
        int cap = instanceInches + maxSize;
        const int INF = int.MaxValue / 2;
        var best = new int[cap + 1];
        var pickFrom = new int[cap + 1]; // index into sizes for backtrace
        for (int i = 1; i <= cap; i++) best[i] = INF;
        for (int s = 1; s <= cap; s++)
        {
            for (int i = 0; i < sizes.Count; i++)
            {
                int sz = sizes[i];
                if (sz > s) continue;
                int prev = best[s - sz];
                if (prev == INF) continue;
                if (prev + 1 < best[s])
                {
                    best[s] = prev + 1;
                    pickFrom[s] = i;
                }
            }
        }

        int chosen;
        if (best[instanceInches] < INF)
        {
            // Tier 1: zero-waste exact fit. Always preferred even when it costs more pieces
            // than a non-exact cover (designers care about material when an exact option exists).
            chosen = instanceInches;
        }
        else
        {
            // Tiers 2 & 3: fewest pieces; tie-break on smallest s (least overage).
            chosen = -1;
            int chosenPieces = INF;
            for (int s = instanceInches; s <= cap; s++)
            {
                if (best[s] < chosenPieces)
                {
                    chosenPieces = best[s];
                    chosen = s;
                }
            }
            if (chosen < 0) yield break; // unreachable when sizes is non-empty
        }

        // Walk back and emit. Order doesn't matter for the caller (pooled into a dict).
        int cur = chosen;
        while (cur > 0)
        {
            int sz = sizes[pickFrom[cur]];
            yield return sz;
            cur -= sz;
        }
    }

    /// <summary>
    /// Minimum reusable offcut length for pool= mode. Offcuts shorter than this become scrap
    /// instead of going back to the pool. Tuned empirically against representative jobs
    /// (see Specs/TCList.xlsx) — below 18" the savings curve is mostly flat, above 24" it
    /// degrades fast as common short-instance lengths can no longer be served from offcuts.
    /// </summary>
    public const int PoolMinOffcutInches = 18;

    /// <summary>
    /// Pool covering across all instances of a slot. Each instance L is shaped per
    /// <see cref="CoverInstance"/> (interior pieces consume full sticks; the trailing piece is
    /// partial). The partial piece is sourced from the smallest fitting offcut in a shared pool;
    /// if none fits, a fresh stick of the trailing piece's size is opened. Tails ≥
    /// <see cref="PoolMinOffcutInches"/> go back to the pool. Instances are processed by length
    /// descending so large trailing pieces produce big reusable tails before short residuals
    /// claim them.
    /// </summary>
    /// <returns>Dictionary of stock size (inches) → sticks purchased.</returns>
    public static Dictionary<int, int> PoolCoverSlot(
        IReadOnlyDictionary<int, int> instanceBuckets,
        IReadOnlyList<int> sizes)
    {
        var sticks = new Dictionary<int, int>();
        foreach (var sz in sizes) sticks[sz] = 0;
        if (instanceBuckets.Count == 0 || sizes.Count == 0) return sticks;

        var instances = new List<int>();
        foreach (var kv in instanceBuckets)
        {
            if (kv.Key <= 0) continue;
            for (int i = 0; i < kv.Value; i++) instances.Add(kv.Key);
        }
        instances.Sort((a, b) => b.CompareTo(a));

        var offcuts = new List<int>();
        foreach (int L in instances)
        {
            var pieces = CoverInstance(L, sizes).ToList();
            if (pieces.Count == 0) continue;
            int sumInterior = 0;
            for (int i = 0; i < pieces.Count - 1; i++)
            {
                sticks[pieces[i]]++;
                sumInterior += pieces[i];
            }
            int last = pieces[pieces.Count - 1];
            int c = L - sumInterior;
            if (c <= 0) continue;

            // smallest fitting offcut
            int bestIdx = -1, bestSize = int.MaxValue;
            for (int i = 0; i < offcuts.Count; i++)
            {
                int o = offcuts[i];
                if (o >= c && o < bestSize) { bestIdx = i; bestSize = o; }
            }
            int tail;
            if (bestIdx >= 0)
            {
                int o = offcuts[bestIdx];
                offcuts.RemoveAt(bestIdx);
                tail = o - c;
            }
            else
            {
                sticks[last]++;
                tail = last - c;
            }
            if (tail >= PoolMinOffcutInches) offcuts.Add(tail);
        }
        return sticks;
    }

    /// <summary>
    /// Single source of truth for how a length token splits a slot's instances: yields
    /// (CutInches, Qty) buckets — the resolved cut LENGTHS and their pooled quantities — sorted by
    /// length ascending. Dispatches the pool= / sizes= / max+min (and bare-token) sub-modes.
    /// <para>
    /// Callers that want part strings render each bucket through <see cref="Resolve"/> (see
    /// <see cref="ExpandSlot"/>); the Worksheet-sync path reads CutInches directly to sort rows.
    /// Keeping the split here — not duplicated per caller — is what stops the SKU/qty/sort of a
    /// rebuild and an incremental update from drifting apart (the pool= mode in particular has no
    /// standalone re-derivation to forget).
    /// </para>
    /// Precondition: <see cref="HasToken"/> is true for <paramref name="template"/>; blank and
    /// untokenized templates are the caller's concern. A tokened template with no cut mode (a bare
    /// <c>{xx"}</c>) yields one bucket per unique instance length.
    /// <para>
    /// For the whole-foot formats every made-to-length cut rounds UP to a foot (see
    /// <see cref="SplitInstance"/>), so cuts already sit on the orderable increment before being
    /// pooled on inches — two instances that both land on 2' (e.g. one clamped up from 8", one a
    /// natural 24") pool into a single 2'×2 bucket right here. <see cref="MergeByRenderedSku"/> is a
    /// final safety net over the result: it guarantees one row per rendered SKU even for an odd
    /// <c>sizes=</c>/<c>pool=</c> template whose distinct stock lengths happen to share a foot band.
    /// </para>
    /// </summary>
    public static IEnumerable<(int CutInches, int Qty)> ExpandTokenBuckets(
        string template, IReadOnlyDictionary<int, int> linearLengthBuckets)
    {
        var pool = ParsePool(template);
        if (pool is not null)
        {
            var sticks = PoolCoverSlot(linearLengthBuckets, pool);
            return MergeByRenderedSku(template, sticks.Where(p => p.Value > 0).Select(p => (p.Key, p.Value)));
        }

        var sizes = ParseSizes(template);
        int? max = sizes is null ? ParseMaxInches(template) : null;
        int? min = sizes is null ? ParseMinInches(template) : null;
        int gran = sizes is null ? GranularityInches(template) : 1;
        var pooled = new Dictionary<int, int>();
        foreach (var kv in linearLengthBuckets)
        {
            int instanceInches = kv.Key;
            int instanceCount = kv.Value;
            var pieces = sizes is null
                ? SplitInstance(instanceInches, max, min, gran)
                : CoverInstance(instanceInches, sizes);
            foreach (int cut in pieces)
            {
                pooled.TryGetValue(cut, out var n);
                pooled[cut] = n + instanceCount;
            }
        }

        return MergeByRenderedSku(template, pooled.Select(p => (p.Key, p.Value)));
    }

    /// <summary>
    /// Safety net that collapses cut buckets rendering to the SAME SKU into one bucket, summing qty,
    /// so the quote never shows a part number on two lines. The split path (bare/max/min) rounds cuts
    /// to the format's orderable increment before pooling, so its buckets already render one-to-one
    /// and this is a no-op there; it earns its keep only when a <c>sizes=</c>/<c>pool=</c> template's
    /// distinct stock lengths share a rendered SKU. The smallest contributing inch value is kept as
    /// each group's representative so callers that render or sort by CutInches stay stable and
    /// ascending-by-length.
    /// </summary>
    private static List<(int CutInches, int Qty)> MergeByRenderedSku(
        string template, IEnumerable<(int CutInches, int Qty)> buckets)
    {
        // Ascending inch order guarantees the first insert for each SKU carries the smallest inches.
        var bySku = new Dictionary<string, (int Inches, int Qty)>(StringComparer.Ordinal);
        foreach (var (inches, qty) in buckets.OrderBy(b => b.CutInches))
        {
            string sku = Resolve(template, inches);
            if (bySku.TryGetValue(sku, out var acc))
                bySku[sku] = (acc.Inches, acc.Qty + qty);
            else
                bySku[sku] = (inches, qty);
        }
        return bySku.Values
            .OrderBy(v => v.Inches)
            .Select(v => (v.Inches, v.Qty))
            .ToList();
    }

    /// <summary>
    /// Single source of truth for slot expansion into part strings. Yields (ResolvedSku, Qty) pairs
    /// sorted by length ascending for token templates, or one (template, fixture.Count) pair for
    /// untokenized templates. Blank templates yield nothing. The length split is delegated to
    /// <see cref="ExpandTokenBuckets"/>; this method only renders each bucket to a SKU.
    /// </summary>
    public static IEnumerable<(string ResolvedSku, int Qty)> ExpandSlot(CountsFixtureModel fixture, int slotIndex)
    {
        string template = fixture.CatalogNumbers[slotIndex] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template)) yield break;

        if (!HasToken(template))
        {
            yield return (template, fixture.Count);
            yield break;
        }

        foreach (var (cutInches, qty) in ExpandTokenBuckets(template, fixture.LinearLengthBuckets))
            yield return (Resolve(template, cutInches), qty);
    }
}

/// <summary>
/// Per-slot length math summary used by the Length section of the hidden Calculations sheet.
/// Mode is "" when the slot has
/// no length token; "sizes"/"max"/"plain" otherwise (min= is a modifier, not a distinct mode).
/// sizes= and pool= strand material by over-covering; the max/plain paths strand material when a
/// min= floor clamps a short cut up, or when a whole-foot format rounds a made-to-length cut up to
/// the next foot — so SuppliedInches counts the ordered feet, not the raw inch cut.
/// </summary>
public sealed record SlotWasteStats(string Mode, int InstanceCount, int UsedInches, int SuppliedInches)
{
    public int WasteInches => SuppliedInches - UsedInches;
}

public static class CatalogWasteAnalyzer
{
    public static SlotWasteStats ComputeSlotWaste(CountsFixtureModel fixture, int slotIndex)
    {
        string template = fixture.CatalogNumbers[slotIndex] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template) || !CatalogLengthTokenResolver.HasToken(template))
            return new SlotWasteStats(string.Empty, 0, 0, 0);

        var pool = CatalogLengthTokenResolver.ParsePool(template);
        var sizes = pool is null ? CatalogLengthTokenResolver.ParseSizes(template) : null;
        bool splitPath = pool is null && sizes is null;
        int? max = splitPath ? CatalogLengthTokenResolver.ParseMaxInches(template) : null;
        int? min = splitPath ? CatalogLengthTokenResolver.ParseMinInches(template) : null;
        // Whole-foot formats round each made-to-length cut up to a foot, so the material actually
        // supplied (and thus waste) is measured in ordered feet, not the raw inch cut.
        int gran = splitPath ? CatalogLengthTokenResolver.GranularityInches(template) : 1;
        string mode = pool is not null ? "pool"
                    : sizes is not null ? "sizes"
                    : max.HasValue ? "max" : "plain";

        int instCount = 0, used = 0;
        foreach (var kv in fixture.LinearLengthBuckets)
        {
            int L = kv.Key, N = kv.Value;
            if (L <= 0) continue;
            instCount += N;
            used += L * N;
        }

        int supplied;
        if (pool is not null)
        {
            var sticks = CatalogLengthTokenResolver.PoolCoverSlot(fixture.LinearLengthBuckets, pool);
            supplied = sticks.Sum(kv => kv.Key * kv.Value);
        }
        else
        {
            supplied = 0;
            foreach (var kv in fixture.LinearLengthBuckets)
            {
                int L = kv.Key, N = kv.Value;
                if (L <= 0) continue;
                int suppliedForThis = 0;
                var pieces = sizes is null
                    ? CatalogLengthTokenResolver.SplitInstance(L, max, min, gran)
                    : CatalogLengthTokenResolver.CoverInstance(L, sizes);
                foreach (int p in pieces) suppliedForThis += p;
                supplied += suppliedForThis * N;
            }
        }
        return new SlotWasteStats(mode, instCount, used, supplied);
    }
}

public class CatalogLengthTokenParseException : Exception
{
    public string RawInput { get; }
    public CatalogLengthTokenParseException(string rawInput, string reason) : base(reason)
    {
        RawInput = rawInput;
    }
}

public record CatalogLengthTokenValidationError(string TypeMark, int Slot, string RawInput, string Reason);

public class CatalogLengthTokenValidationException : Exception
{
    public IReadOnlyList<CatalogLengthTokenValidationError> Errors { get; }
    public CatalogLengthTokenValidationException(IReadOnlyList<CatalogLengthTokenValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<CatalogLengthTokenValidationError> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Catalog Number length-token validation failed:");
        sb.AppendLine();
        foreach (var e in errors)
        {
            sb.AppendLine($"  Type \"{e.TypeMark}\", Catalog Number{e.Slot} = \"{e.RawInput}\"");
            sb.AppendLine($"    → {e.Reason}");
        }
        sb.AppendLine();
        sb.Append("Fix the family parameters and re-export.");
        return sb.ToString();
    }
}

public static class CatalogLengthTokenValidator
{
    /// <summary>
    /// Validates length tokens in every CatalogNumberX across all fixtures, plus the cross-checks:
    ///   - token present but the Type has no positive-Linear-Length instances → reject;
    ///   - token present in the same slot as a non-blank CatalogQtyX → reject (incoherent).
    /// </summary>
    public static void ValidateOrThrow(IEnumerable<CountsFixtureModel> fixtures)
    {
        var errors = new List<CatalogLengthTokenValidationError>();
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string raw = f.CatalogNumbers[c] ?? string.Empty;
                if (!CatalogLengthTokenResolver.HasToken(raw)) continue;

                try
                {
                    CatalogLengthTokenResolver.Validate(raw);
                }
                catch (CatalogLengthTokenParseException ex)
                {
                    errors.Add(new CatalogLengthTokenValidationError(f.TypeMark, c + 1, raw, ex.Message));
                    continue;
                }

                if (f.LinearLengthBuckets.Count == 0)
                {
                    errors.Add(new CatalogLengthTokenValidationError(f.TypeMark, c + 1, raw,
                        "Length token requires instances with a positive Linear Length"));
                    continue;
                }

                string qty = f.CatalogQtys[c] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(qty))
                {
                    errors.Add(new CatalogLengthTokenValidationError(f.TypeMark, c + 1, raw,
                        $"Length token cannot be combined with Catalog Qty{c + 1} = \"{qty}\""));
                }
            }
        }
        if (errors.Count > 0)
            throw new CatalogLengthTokenValidationException(errors);
    }
}
