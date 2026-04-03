using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TurboSuite.Docs.Models;

public class LoadsCircuitModel
{
    private string _circuitNumber = string.Empty;
    public string CircuitNumber
    {
        get => _circuitNumber;
        set => _circuitNumber = string.Equals(value, "<unnamed>", StringComparison.OrdinalIgnoreCase) ? "<...>" : value;
    }
    public string LoadName { get; set; } = string.Empty;
    public string LoadClassification { get; set; } = string.Empty;
    public double ApparentLoadVA { get; set; }
    public string TotalWattsDisplay => $"{Math.Round(ApparentLoadVA)} W";
    public List<LoadsFixtureGroup> FixtureGroups { get; set; } = new();
    public List<string> DriverSwitchIDs { get; set; } = new();

    public string FixturesDisplay => BuildFixturesDisplay();
    public string QuantityDisplay => BuildQuantityDisplay();
    public string DriverDisplay => BuildDriverDisplay();

    private string BuildFixturesDisplay()
    {
        if (FixtureGroups.Count == 0) return string.Empty;

        var typeMarks = FixtureGroups.Select(g => g.TypeMark).ToList();

        // All same TypeMark
        if (typeMarks.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return typeMarks[0];

        // Group by alpha prefix (strip trailing digits)
        var groups = typeMarks
            .GroupBy(tm => Regex.Replace(tm, @"\d+$", ""), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var group in groups)
        {
            var marks = group.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            if (marks.Count == 1)
                parts.Add(marks[0]);
            else
                parts.Add($"{group.Key}#");
        }

        return string.Join(",", parts);
    }

    private string BuildQuantityDisplay()
    {
        if (FixtureGroups.Count == 0) return string.Empty;

        bool hasLinear = FixtureGroups.Any(g => g.IsLinear);
        bool hasPoint = FixtureGroups.Any(g => !g.IsLinear);

        if (hasLinear && hasPoint)
        {
            int pointCount = FixtureGroups.Where(g => !g.IsLinear).Sum(g => g.Quantity);
            double linearTotal = FixtureGroups.Where(g => g.IsLinear).Sum(g => g.TotalLinearLengthFeet);
            return $"{pointCount} + {linearTotal:F1}'";
        }

        if (hasLinear)
        {
            double total = FixtureGroups.Sum(g => g.TotalLinearLengthFeet);
            return $"{total:F1}'";
        }

        int qty = FixtureGroups.Sum(g => g.Quantity);
        return qty.ToString();
    }

    private string BuildDriverDisplay()
    {
        if (DriverSwitchIDs.Count == 0) return string.Empty;

        var sorted = DriverSwitchIDs
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sorted.Count == 0) return string.Empty;

        // Parse each ID into prefix + optional single-letter suffix
        var parsed = sorted.Select(id =>
        {
            if (id.Length > 1 && char.IsLower(id[^1]))
                return (Prefix: id[..^1], Suffix: id[^1], HasSuffix: true);
            return (Prefix: id, Suffix: '\0', HasSuffix: false);
        }).ToList();

        // Group by prefix
        var groups = parsed
            .GroupBy(p => p.Prefix, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var group in groups)
        {
            var withSuffix = group.Where(p => p.HasSuffix).OrderBy(p => p.Suffix).ToList();
            var withoutSuffix = group.Where(p => !p.HasSuffix).ToList();

            // Standalone entries (no suffix)
            foreach (var entry in withoutSuffix)
                parts.Add(entry.Prefix);

            if (withSuffix.Count == 0) continue;

            if (withSuffix.Count == 1)
            {
                parts.Add($"{group.Key}{withSuffix[0].Suffix}");
                continue;
            }

            // Check if suffixes are consecutive
            bool consecutive = true;
            for (int i = 1; i < withSuffix.Count; i++)
            {
                if (withSuffix[i].Suffix - withSuffix[i - 1].Suffix != 1)
                {
                    consecutive = false;
                    break;
                }
            }

            if (consecutive)
                parts.Add($"{group.Key}{withSuffix[0].Suffix}-{withSuffix[^1].Suffix}");
            else
                parts.AddRange(withSuffix.Select(s => $"{group.Key}{s.Suffix}"));
        }

        return string.Join(",", parts);
    }
}

public class LoadsFixtureGroup
{
    public string TypeMark { get; set; } = string.Empty;
    public bool IsLinear { get; set; }
    public int Quantity { get; set; }
    public double TotalLinearLengthFeet { get; set; }
}
