#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// Pure text builders for the per-type breakdown TurboSchedule shows on hover: the header's type list and
/// the tooltip on a ⟨varies⟩ or n/a field (which type holds which value / which type lacks the parameter).
/// Kept Revit-free so the wording is pinned by tests; the shim collector supplies the per-type facts.
/// </summary>
public static class SpecTypeBreakdown
{
    /// <summary>Header line: types grouped under their family, families in first-seen order —
    /// e.g. <c>Downlight: 2700K, 3000K (not placed) · Cove: Std</c>.</summary>
    public static string Summary(IReadOnlyList<SpecTypeRef> types)
    {
        if (types == null || types.Count == 0) return "";
        var families = new List<string>();
        var byFamily = new Dictionary<string, List<string>>();
        foreach (var t in types)
        {
            if (!byFamily.TryGetValue(t.FamilyName, out var list))
            {
                byFamily[t.FamilyName] = list = new List<string>();
                families.Add(t.FamilyName);
            }
            list.Add(t.IsPlaced ? t.TypeName : $"{t.TypeName} (not placed)");
        }
        return string.Join(" · ", families.Select(f => $"{f}: {string.Join(", ", byFamily[f])}"));
    }

    /// <summary>Header hover: one "Family: Type" per line.</summary>
    public static string List(IReadOnlyList<SpecTypeRef> types) =>
        types == null || types.Count == 0 ? null : string.Join("\n", types.Select(t => t.Label));

    /// <summary>⟨varies⟩ tooltip: each type's current value. Yes/No renders as Yes/No; empty as (blank).</summary>
    public static string Varies(IEnumerable<(SpecTypeRef Type, string Value)> rows, SpecValueKind kind)
    {
        var lines = rows.Select(r => $"{r.Type.Label} — {Display(r.Value, kind)}");
        return "Values differ across the types with this Type Mark:\n" + string.Join("\n", lines) +
               "\n\nType a value to apply it to all of them.";
    }

    /// <summary>n/a tooltip: the types the parameter is missing from.</summary>
    public static string Missing(IEnumerable<SpecTypeRef> missingFrom) =>
        "Parameter missing from:\n" + string.Join("\n", missingFrom.Select(t => t.Label)) +
        "\n\nAdd it to the family (or fix the Type Mark) to edit this field.";

    /// <summary>n/a tooltip for a storage-type disagreement: each type's parameter kind.</summary>
    public static string StorageMismatch(IEnumerable<(SpecTypeRef Type, string StorageKind)> rows) =>
        "Parameter type differs across the types with this Type Mark:\n" +
        string.Join("\n", rows.Select(r => $"{r.Type.Label} — {r.StorageKind}")) +
        "\n\nMake the families use the same parameter to edit this field.";

    private static string Display(string value, SpecValueKind kind)
    {
        if (kind == SpecValueKind.Boolean)
            return value == "1" ? "Yes" : value == "0" ? "No" : "(blank)";
        return string.IsNullOrWhiteSpace(value) ? "(blank)" : value;
    }
}
