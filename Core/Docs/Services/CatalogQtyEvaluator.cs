using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public enum CatalogQtyMode { Default, PerFixture, RatioPerFixture, FixedPerType, Length }

public record CatalogQtyRule(CatalogQtyMode Mode, double Value)
{
    /// <summary>
    /// Resolves the per-slot quantity. <paramref name="linearLength"/> is the fixture's total
    /// Linear Length in feet; only the <see cref="CatalogQtyMode.Length"/> stock-cut mode reads it
    /// (the others ignore it). For <c>Length</c>, <see cref="Value"/> is the stock length in feet
    /// (the parser canonicalizes <c>@in</c> inputs to feet), so the math stays unit-agnostic:
    /// padded length (feet) divided by stock (feet).
    /// </summary>
    public int Evaluate(int count, double linearLength) => Mode switch
    {
        CatalogQtyMode.Default => count,
        CatalogQtyMode.PerFixture => (int)Math.Ceiling(count * Value),
        CatalogQtyMode.RatioPerFixture => (int)Math.Ceiling(count / Value),
        CatalogQtyMode.FixedPerType => (int)Value,
        CatalogQtyMode.Length => Value > 0
            ? (int)Math.Ceiling(Math.Ceiling(linearLength * 1.05) / Value)
            : count,
        _ => count,
    };

    public static CatalogQtyRule DefaultRule { get; } = new(CatalogQtyMode.Default, 0);
}

public class CatalogQtyParseException : Exception
{
    public string RawInput { get; }
    public CatalogQtyParseException(string rawInput, string reason)
        : base(reason)
    {
        RawInput = rawInput;
    }
}

public static class CatalogQtyParser
{
    public static CatalogQtyRule Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return CatalogQtyRule.DefaultRule;

        // net48's string.IsNullOrWhiteSpace lacks [NotNullWhen(false)], so flag non-null explicitly.
        var input = raw!.Trim();

        // Trailing keyword suffixes (case-insensitive, optional whitespace before @). None is a
        // suffix of another, so the order is irrelevant. @ft and @in both produce the Length mode;
        // @in is canonicalized to feet (÷ 12) so the rule's Value is always feet downstream.
        if (TryStripSuffix(input, "@type", out var typePrefix))
            return new CatalogQtyRule(CatalogQtyMode.FixedPerType, ParsePositive(raw, typePrefix, "@type"));
        if (TryStripSuffix(input, "@ft", out var ftPrefix))
            return new CatalogQtyRule(CatalogQtyMode.Length, ParsePositive(raw, ftPrefix, "@ft"));
        if (TryStripSuffix(input, "@in", out var inPrefix))
            return new CatalogQtyRule(CatalogQtyMode.Length, ParsePositive(raw, inPrefix, "@in") / 12.0);

        // Ratio form 1/N
        if (input.Contains('/'))
        {
            var parts = input.Split('/');
            if (parts.Length != 2)
                throw new CatalogQtyParseException(raw, "Ratio must be exactly '1/N'");
            var num = parts[0];
            var den = parts[1];
            // Strict: no whitespace inside fraction, numerator must be literal "1"
            if (num != "1")
                throw new CatalogQtyParseException(raw, "Ratio numerator must be 1 (use bare number for >1 per fixture)");
            if (!int.TryParse(den, NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator))
                throw new CatalogQtyParseException(raw, $"Ratio denominator '{den}' must be a positive integer");
            if (denominator <= 0)
                throw new CatalogQtyParseException(raw, "Ratio denominator must be greater than zero");
            return new CatalogQtyRule(CatalogQtyMode.RatioPerFixture, denominator);
        }

        // Bare number
        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var perFixture))
        {
            if (perFixture <= 0)
                throw new CatalogQtyParseException(raw, "Quantity must be greater than zero");
            return new CatalogQtyRule(CatalogQtyMode.PerFixture, perFixture);
        }

        // Common mistake hints
        if (input.Contains("type", StringComparison.OrdinalIgnoreCase))
            throw new CatalogQtyParseException(raw, "Missing '@' before 'type'; did you mean '" + input.Replace("type", "@type", StringComparison.OrdinalIgnoreCase) + "'?");
        // @length never shipped — it's retired in favor of the unit-explicit @ft / @in forms.
        if (input.EndsWith("@length", StringComparison.OrdinalIgnoreCase)
            || input.Contains("length", StringComparison.OrdinalIgnoreCase))
            throw new CatalogQtyParseException(raw, "'@length' is not supported — specify a unit: use 'N @ft' or 'N @in'");

        throw new CatalogQtyParseException(raw, "Unrecognized format (expected blank, N, 1/N, N @type, N @ft, or N @in)");
    }

    // Splits a trailing keyword suffix (e.g. "@ft") off the input, trimming whitespace before it.
    private static bool TryStripSuffix(string input, string suffix, out string prefix)
    {
        if (input.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            prefix = input.Substring(0, input.Length - suffix.Length).TrimEnd();
            return true;
        }
        prefix = string.Empty;
        return false;
    }

    // Parses the numeric prefix that precedes a keyword suffix, enforcing N > 0.
    private static double ParsePositive(string raw, string prefix, string suffix)
    {
        if (prefix.Length == 0)
            throw new CatalogQtyParseException(raw, $"Missing quantity before '{suffix}'");
        if (!double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new CatalogQtyParseException(raw, $"Invalid number '{prefix}' before '{suffix}'");
        if (value <= 0)
            throw new CatalogQtyParseException(raw, "Quantity must be greater than zero");
        return value;
    }
}

public record CatalogQtyValidationError(string TypeMark, int Slot, string RawInput, string Reason);

public class CatalogQtyValidationException : Exception
{
    public IReadOnlyList<CatalogQtyValidationError> Errors { get; }

    public CatalogQtyValidationException(IReadOnlyList<CatalogQtyValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<CatalogQtyValidationError> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Catalog Qty validation failed:");
        sb.AppendLine();
        foreach (var e in errors)
        {
            sb.AppendLine($"  Type \"{e.TypeMark}\", Catalog Qty{e.Slot} = \"{e.RawInput}\"");
            sb.AppendLine($"    → {e.Reason}");
        }
        sb.AppendLine();
        sb.Append("Fix the family parameters and re-export.");
        return sb.ToString();
    }
}

public static class CatalogQtyValidator
{
    /// <summary>
    /// Parses every non-blank Catalog QtyX value across all fixtures.
    /// Throws CatalogQtyValidationException if any fail.
    /// </summary>
    public static void ValidateOrThrow(IEnumerable<CountsFixtureModel> fixtures)
    {
        var errors = new List<CatalogQtyValidationError>();
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                var raw = f.CatalogQtys[c];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                CatalogQtyRule rule;
                try
                {
                    rule = CatalogQtyParser.Parse(raw);
                }
                catch (CatalogQtyParseException ex)
                {
                    errors.Add(new CatalogQtyValidationError(f.TypeMark, c + 1, raw, ex.Message));
                    continue;
                }

                // Semantic check: the Length stock-cut mode (N @ft / N @in) divides the fixture's
                // padded Linear Length by the stock length — meaningless without a positive Linear.
                if (rule.Mode == CatalogQtyMode.Length && f.LinearLength <= 0)
                    errors.Add(new CatalogQtyValidationError(f.TypeMark, c + 1, raw,
                        "Stock-length qty (N @ft / N @in) requires a positive Linear Length on the fixture instances"));
            }
        }
        if (errors.Count > 0)
            throw new CatalogQtyValidationException(errors);
    }
}
