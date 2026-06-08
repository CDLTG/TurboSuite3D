using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public enum CatalogQtyMode { Default, PerFixture, RatioPerFixture, FixedPerType }

public record CatalogQtyRule(CatalogQtyMode Mode, double Value)
{
    public int Evaluate(int fixtureCount) => Mode switch
    {
        CatalogQtyMode.Default => fixtureCount,
        CatalogQtyMode.PerFixture => (int)Math.Ceiling(fixtureCount * Value),
        CatalogQtyMode.RatioPerFixture => (int)Math.Ceiling(fixtureCount / Value),
        CatalogQtyMode.FixedPerType => (int)Value,
        _ => fixtureCount,
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

        // Trailing @type suffix (case-insensitive, optional whitespace before @)
        if (input.EndsWith("@type", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = input.Substring(0, input.Length - "@type".Length).TrimEnd();
            if (prefix.Length == 0)
                throw new CatalogQtyParseException(raw, "Missing quantity before '@type'");
            if (!double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out var fixedQty))
                throw new CatalogQtyParseException(raw, $"Invalid number '{prefix}' before '@type'");
            if (fixedQty <= 0)
                throw new CatalogQtyParseException(raw, "Quantity must be greater than zero");
            return new CatalogQtyRule(CatalogQtyMode.FixedPerType, fixedQty);
        }

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

        throw new CatalogQtyParseException(raw, "Unrecognized format (expected blank, N, 1/N, or N @type)");
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
                try
                {
                    CatalogQtyParser.Parse(raw);
                }
                catch (CatalogQtyParseException ex)
                {
                    errors.Add(new CatalogQtyValidationError(f.TypeMark, c + 1, raw, ex.Message));
                }
            }
        }
        if (errors.Count > 0)
            throw new CatalogQtyValidationException(errors);
    }
}
