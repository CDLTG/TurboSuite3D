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
/// Formats (case-sensitive): <c>xx</c> → <c>{inches}"</c>; <c>XX</c> → <c>{inches}IN</c>;
/// <c>x-x</c> → <c>{feet}'-{inches}"</c>; <c>X-X</c> → <c>{feet}FT-{inches}IN</c>.
/// Options (mutually exclusive):
///   <c>max=N</c> — made-to-length: greedy-splits each instance into N-sized cuts plus one remainder.
///   <c>sizes=N1|N2|...</c> — discrete stock sizes (e.g. 96|48): covers each instance with the
///     fewest sticks whose sum ≥ instance length, tie-breaking on least overage.
/// Unknown formats / option keys / non-integer values / combined max+sizes fail validation.
/// </summary>
public static class CatalogLengthTokenResolver
{
    // {xx}, {XX}, {x-x}, {X-X} with optional comma-separated options.
    // Lowercase tokens emit ASCII unit marks (", '-"); uppercase emit literal letters (IN, FT-IN).
    private static readonly Regex TokenRegex = new(
        @"\{(?<fmt>xx|XX|x-x|X-X)(?:,(?<opts>[^}]*))?\}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFormats = new(StringComparer.Ordinal)
    {
        "xx",
        "XX",
        "x-x",
        "X-X",
    };

    public sealed record ParsedToken(string Format, int? MaxInches, string Raw, int Index, int Length);

    public static bool HasToken(string? catalogNumber)
    {
        if (string.IsNullOrEmpty(catalogNumber)) return false;
        return catalogNumber.Contains("{x", StringComparison.Ordinal)
            || catalogNumber.Contains("{X", StringComparison.Ordinal)
            ? TokenRegex.IsMatch(catalogNumber)
            : false;
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
            int idx = -1;
            int ix = catalogNumber.IndexOf("{x", cursor, StringComparison.Ordinal);
            int iX = catalogNumber.IndexOf("{X", cursor, StringComparison.Ordinal);
            if (ix >= 0 && (iX < 0 || ix <= iX)) idx = ix;
            else if (iX >= 0) idx = iX;
            if (idx < 0) break;
            var m = TokenRegex.Match(catalogNumber, idx);
            if (!m.Success || m.Index != idx)
                throw new CatalogLengthTokenParseException(catalogNumber, "Malformed length token (expected '{xx}', '{XX}', '{x-x}', '{X-X}', or with ',max=N' / ',sizes=N|N|...')");
            ValidateToken(catalogNumber, m);
            cursor = m.Index + m.Length;
        }
    }

    private static void ValidateToken(string raw, Match m)
    {
        string fmt = m.Groups["fmt"].Value;
        if (!KnownFormats.Contains(fmt))
            throw new CatalogLengthTokenParseException(raw, $"Unknown length format '{fmt}' (supported: xx, XX, x-x, X-X — case-sensitive)");

        if (!m.Groups["opts"].Success) return;

        string opts = m.Groups["opts"].Value;
        bool sawMax = false, sawSizes = false;
        foreach (var part in opts.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                throw new CatalogLengthTokenParseException(raw, $"Malformed option '{part}' (expected 'max=<int>' or 'sizes=N|N|...')");
            string key = kv[0].Trim();
            string val = kv[1].Trim();

            if (string.Equals(key, "max", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    throw new CatalogLengthTokenParseException(raw, $"max='{val}' must be a bare positive integer (inches)");
                if (n <= 0)
                    throw new CatalogLengthTokenParseException(raw, "max must be greater than zero");
                sawMax = true;
            }
            else if (string.Equals(key, "sizes", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(val))
                    throw new CatalogLengthTokenParseException(raw, "sizes must list at least one positive integer (e.g. sizes=96|48)");
                var seen = new HashSet<int>();
                foreach (var s in val.Split('|'))
                {
                    if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn))
                        throw new CatalogLengthTokenParseException(raw, $"sizes entry '{s.Trim()}' must be a positive integer (inches)");
                    if (sn <= 0)
                        throw new CatalogLengthTokenParseException(raw, "sizes entries must be greater than zero");
                    if (!seen.Add(sn))
                        throw new CatalogLengthTokenParseException(raw, $"sizes entry '{sn}' is duplicated");
                }
                sawSizes = true;
            }
            else
            {
                throw new CatalogLengthTokenParseException(raw, $"Unknown option '{key}' (supported: max, sizes)");
            }
        }

        if (sawMax && sawSizes)
            throw new CatalogLengthTokenParseException(raw, "max and sizes cannot both be set on the same token (made-to-length vs. discrete stock are different modes)");
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
    /// Reads the first token's sizes= list (descending), or null if absent. Caller must have
    /// already validated the template.
    /// </summary>
    public static IReadOnlyList<int>? ParseSizes(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var m = TokenRegex.Match(template);
        if (!m.Success || !m.Groups["opts"].Success) return null;
        foreach (var part in m.Groups["opts"].Value.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (!string.Equals(kv[0].Trim(), "sizes", StringComparison.OrdinalIgnoreCase)) continue;
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
                return $"{inches}\"";
            case "XX":
                return $"{inches}IN";
            case "x-x":
            {
                int feet = inches / 12;
                int rem = inches % 12;
                return $"{feet}'-{rem}\"";
            }
            case "X-X":
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
    /// L=120, max=null → [120].
    /// </summary>
    public static IEnumerable<int> SplitInstance(int instanceInches, int? maxInches)
    {
        if (instanceInches <= 0) yield break;
        if (!maxInches.HasValue || instanceInches <= maxInches.Value)
        {
            yield return instanceInches;
            yield break;
        }
        int n = maxInches.Value;
        int full = instanceInches / n;
        int rem = instanceInches % n;
        for (int i = 0; i < full; i++) yield return n;
        if (rem > 0) yield return rem;
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
    /// Single source of truth for slot expansion. Yields (ResolvedSku, Qty) pairs sorted
    /// by length ascending for token templates, or one (template, fixture.Count) pair for
    /// untokenized templates. Blank templates yield nothing.
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

        var sizes = ParseSizes(template);
        int? max = sizes is null ? ParseMaxInches(template) : null;
        var pooled = new Dictionary<int, int>();
        foreach (var kv in fixture.LinearLengthBuckets)
        {
            int instanceInches = kv.Key;
            int instanceCount = kv.Value;
            var pieces = sizes is null
                ? SplitInstance(instanceInches, max)
                : CoverInstance(instanceInches, sizes);
            foreach (int cut in pieces)
            {
                pooled.TryGetValue(cut, out var n);
                pooled[cut] = n + instanceCount;
            }
        }

        foreach (var kv in pooled.OrderBy(p => p.Key))
            yield return (Resolve(template, kv.Key), kv.Value);
    }
}

/// <summary>
/// Per-slot length math summary used by the hidden Waste sheet. Mode is "" when the slot has
/// no length token; "sizes"/"max"/"plain" otherwise. Only the sizes mode can produce waste &gt; 0.
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

        var sizes = CatalogLengthTokenResolver.ParseSizes(template);
        int? max = sizes is null ? CatalogLengthTokenResolver.ParseMaxInches(template) : null;
        string mode = sizes is not null ? "sizes" : (max.HasValue ? "max" : "plain");

        int instCount = 0, used = 0, supplied = 0;
        foreach (var kv in fixture.LinearLengthBuckets)
        {
            int L = kv.Key, N = kv.Value;
            if (L <= 0) continue;
            instCount += N;
            used += L * N;
            int suppliedForThis = 0;
            var pieces = sizes is null
                ? CatalogLengthTokenResolver.SplitInstance(L, max)
                : CatalogLengthTokenResolver.CoverInstance(L, sizes);
            foreach (int p in pieces) suppliedForThis += p;
            supplied += suppliedForThis * N;
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
