#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// Reads placed lighting fixtures (<c>OST_LightingFixtures</c>) and drivers
/// (<c>OST_LightingDevices</c>), groups their distinct <see cref="FamilySymbol"/>s by Type Mark, and
/// reconciles each <see cref="FieldDef"/> across a group into a <see cref="SpecField"/>. Mirrors the
/// dedupe/sort of <c>TurboDocs</c>'s <c>ScheduleCollectorService</c> but builds the editable model.
/// </summary>
public static class ScheduleTypeCollector
{
    private static readonly (BuiltInCategory Cat, PageKind Kind)[] Sources =
    {
        (BuiltInCategory.OST_LightingFixtures, PageKind.Fixture),
        (BuiltInCategory.OST_LightingDevices, PageKind.Driver),
    };

    public static List<FixtureTypeSpec> Collect(Document doc)
    {
        var pages = new List<FixtureTypeSpec>();

        foreach (var (cat, kind) in Sources)
        {
            foreach (var group in SymbolsByTypeMark(doc, cat))
            {
                var fields = FieldDef.Roster
                    .Where(d => d.AppliesTo(kind))
                    .Select(d => BuildField(d, group.Value))
                    .ToList();
                pages.Add(new FixtureTypeSpec(group.Key, kind, fields));
            }
        }

        // Fixtures and drivers fully interleaved; Kind only breaks an exact Type-Mark tie.
        pages.Sort((a, b) =>
        {
            int c = string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });
        return pages;
    }

    /// <summary>Distinct placed symbols of a category, grouped by non-blank Type Mark. Shared by the
    /// writer so save-time re-resolution sees the same membership.</summary>
    public static SortedDictionary<string, List<FamilySymbol>> SymbolsByTypeMark(Document doc, BuiltInCategory cat)
    {
        var byMark = new SortedDictionary<string, List<FamilySymbol>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<ElementId>();

        var instances = new FilteredElementCollector(doc)
            .OfCategory(cat)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in instances)
        {
            var symbol = fi.Symbol;
            if (symbol == null || !seen.Add(symbol.Id)) continue;

            var tm = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string typeMark = (tm is { HasValue: true }) ? tm.AsString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(typeMark)) continue;

            if (!byMark.TryGetValue(typeMark, out var lst))
                byMark[typeMark] = lst = new List<FamilySymbol>();
            lst.Add(symbol);
        }
        return byMark;
    }

    private static SpecField BuildField(FieldDef def, List<FamilySymbol> symbols)
    {
        var field = new SpecField(def);

        var prms = symbols.Select(s => Resolve(s, def)).ToList();

        // n/a dominates: absent on any symbol, or symbols disagree on storage type.
        if (prms.Any(p => p == null))
        {
            field.IsNa = true;
            return field;
        }
        var storageTypes = prms.Select(p => p.StorageType).Distinct().ToList();
        if (storageTypes.Count > 1)
        {
            field.IsNa = true;
            return field;
        }

        field.ValueKind =
            storageTypes[0] == StorageType.String ? SpecValueKind.Text
            : storageTypes[0] == StorageType.Integer && IsYesNo(prms[0]) ? SpecValueKind.Boolean
            : SpecValueKind.Numeric;
        field.IsReadOnly = prms.Any(p => p.IsReadOnly);

        var values = prms.Select(ReadDisplay).ToList();
        if (values.Distinct().Count() > 1)
        {
            field.IsVaries = true;       // leave Value empty; placeholder ⟨varies⟩
        }
        else
        {
            field.SetInitialValue(values[0]);
        }
        return field;
    }

    /// <summary>Resolve a field's <see cref="Parameter"/> on a symbol; null means absent (→ n/a).</summary>
    public static Parameter Resolve(FamilySymbol symbol, FieldDef def) =>
        Resolve(symbol, def.ParamKey, def.IsBuiltIn);

    public static Parameter Resolve(FamilySymbol symbol, string paramKey, bool isBuiltIn)
    {
        if (isBuiltIn)
        {
            if (!Enum.TryParse(paramKey, out BuiltInParameter bip)) return null;
            return symbol.get_Parameter(bip);
        }
        return symbol.LookupParameter(paramKey);
    }

    /// <summary>True when an Integer param is a Yes/No (boolean) — rendered as a checkbox.</summary>
    private static bool IsYesNo(Parameter p) =>
        p.Definition is InternalDefinition def && def.GetDataType() == SpecTypeId.Boolean.YesNo;

    private static string ReadDisplay(Parameter p)
    {
        string raw = p.StorageType switch
        {
            StorageType.String => p.AsString() ?? "",
            StorageType.Integer => p.AsInteger().ToString(),
            StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString("F2"),
            StorageType.ElementId => p.AsValueString() ?? "",
            _ => ""
        };
        return (raw ?? "").Trim();
    }
}
