using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Grammar (v1): <c>{L:&lt;format&gt;[,max=&lt;int&gt;]}</c>.
/// Formats: <c>in</c> → <c>{inches}"</c>; <c>ft-in</c> → <c>{feet}'-{inches}"</c>.
/// <c>max=N</c> (positive integer inches) greedy-splits each instance length into N-sized cuts
/// plus one remainder. Unknown formats / option keys / non-integer max fail validation.
/// </summary>
public static class CatalogLengthTokenResolver
{
    // {L:<format>[,max=<int>]} — captures format ("in"/"ft-in") and optional max integer.
    // The token grammar is intentionally narrow; anything not matching this fails Validate.
    private static readonly Regex TokenRegex = new(
        @"\{L:(?<fmt>[A-Za-z][A-Za-z\-]*)(?:,(?<opts>[^}]*))?\}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "in",
        "ft-in",
    };

    public sealed record ParsedToken(string Format, int? MaxInches, string Raw, int Index, int Length);

    public static bool HasToken(string? catalogNumber)
    {
        if (string.IsNullOrEmpty(catalogNumber)) return false;
        // Cheap pre-filter — only invoke the regex when "{L:" is present.
        return catalogNumber.Contains("{L:", StringComparison.Ordinal)
            && TokenRegex.IsMatch(catalogNumber);
    }

    /// <summary>
    /// Validates every <c>{L:...}</c> token in <paramref name="catalogNumber"/>.
    /// Throws <see cref="CatalogLengthTokenParseException"/> on the first failure.
    /// </summary>
    public static void Validate(string? catalogNumber)
    {
        if (string.IsNullOrEmpty(catalogNumber)) return;

        // Bare "{L" without a recognized token shape — catches "{L}" / "{Length}" /
        // "{L:in" (missing close) before the regex silently ignores them.
        int cursor = 0;
        while (true)
        {
            int idx = catalogNumber.IndexOf("{L", cursor, StringComparison.Ordinal);
            if (idx < 0) break;
            var m = TokenRegex.Match(catalogNumber, idx);
            if (!m.Success || m.Index != idx)
                throw new CatalogLengthTokenParseException(catalogNumber, "Malformed length token (expected '{L:in}', '{L:ft-in}', or with ',max=<int>')");
            ValidateToken(catalogNumber, m);
            cursor = m.Index + m.Length;
        }
    }

    private static void ValidateToken(string raw, Match m)
    {
        string fmt = m.Groups["fmt"].Value;
        if (!KnownFormats.Contains(fmt))
            throw new CatalogLengthTokenParseException(raw, $"Unknown length format '{fmt}' (supported: in, ft-in)");

        if (!m.Groups["opts"].Success) return;

        string opts = m.Groups["opts"].Value;
        // Single-option v1: only "max=<int>". Anything else fails.
        foreach (var part in opts.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                throw new CatalogLengthTokenParseException(raw, $"Malformed option '{part}' (expected 'max=<int>')");
            string key = kv[0].Trim();
            string val = kv[1].Trim();
            if (!string.Equals(key, "max", StringComparison.OrdinalIgnoreCase))
                throw new CatalogLengthTokenParseException(raw, $"Unknown option '{key}' (only 'max' is supported)");
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                throw new CatalogLengthTokenParseException(raw, $"max='{val}' must be a bare positive integer (inches)");
            if (n <= 0)
                throw new CatalogLengthTokenParseException(raw, "max must be greater than zero");
        }
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
    /// Replaces every <c>{L:...}</c> token in <paramref name="template"/> with the rendered
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
        if (string.Equals(format, "in", StringComparison.OrdinalIgnoreCase))
            return $"{inches}\"";
        if (string.Equals(format, "ft-in", StringComparison.OrdinalIgnoreCase))
        {
            int feet = inches / 12;
            int rem = inches % 12;
            return $"{feet}'-{rem}\"";
        }
        // Validate guards against this branch.
        return inches.ToString(CultureInfo.InvariantCulture);
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

        int? max = ParseMaxInches(template);
        var pooled = new Dictionary<int, int>();
        foreach (var kv in fixture.LinearLengthBuckets)
        {
            int instanceInches = kv.Key;
            int instanceCount = kv.Value;
            foreach (int cut in SplitInstance(instanceInches, max))
            {
                pooled.TryGetValue(cut, out var n);
                pooled[cut] = n + instanceCount;
            }
        }

        foreach (var kv in pooled.OrderBy(p => p.Key))
            yield return (Resolve(template, kv.Key), kv.Value);
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
    /// Validates {L:...} tokens in every CatalogNumberX across all fixtures, plus the cross-checks:
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
