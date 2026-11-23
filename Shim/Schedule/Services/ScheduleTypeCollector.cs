#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Schedule.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// Reads lighting fixture (<c>OST_LightingFixtures</c>) and driver (<c>OST_LightingDevices</c>) types, groups
/// <b>every loaded</b> <see cref="FamilySymbol"/> by Type Mark — placed or not — and reconciles each
/// <see cref="FieldDef"/> across a group into a <see cref="SpecField"/>.
///
/// <para><b>A Type Mark is one specification.</b> Every type carrying a mark is reconciled and written
/// together, unplaced ones included, so a type placed (or swapped to via Type Selector / TurboRPS) after an
/// edit already carries it. Grouping by placed instances only left unplaced siblings unedited — they
/// surfaced later as ⟨varies⟩, n/a, or, once the edited type was swapped out, as the old/blank values.</para>
///
/// <para>A <i>page</i> still exists only for a mark with at least one placed type, so template/library
/// types that were loaded but never used don't add pages; they only join a mark that is in use.</para>
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
            var placed = PlacedSymbolIds(doc, cat);

            foreach (var group in SymbolsByTypeMark(doc, cat))
            {
                if (!group.Value.Any(s => placed.Contains(s.Id)))
                    continue; // no placed type carries this mark — no page

                var types = group.Value
                    .Select(s => new SpecTypeRef(s.FamilyName, s.Name, placed.Contains(s.Id)))
                    .ToList();

                var fields = FieldDef.Roster
                    .Where(d => d.AppliesTo(kind))
                    .Select(d => BuildField(d, group.Value, types))
                    .ToList();

                pages.Add(new FixtureTypeSpec(group.Key, kind, fields, types));
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

    /// <summary>Every loaded symbol of a category — placed or not — grouped by non-blank Type Mark, each
    /// group ordered by family then type (natural). Shared by the writer so a Save writes the same
    /// membership the form reconciled.</summary>
    public static SortedDictionary<string, List<FamilySymbol>> SymbolsByTypeMark(Document doc, BuiltInCategory cat)
    {
        var byMark = new SortedDictionary<string, List<FamilySymbol>>(StringComparer.OrdinalIgnoreCase);

        var symbols = new FilteredElementCollector(doc)
            .OfCategory(cat)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        foreach (var symbol in symbols)
        {
            var tm = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string typeMark = (tm is { HasValue: true }) ? tm.AsString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(typeMark)) continue;

            if (!byMark.TryGetValue(typeMark, out var lst))
                byMark[typeMark] = lst = new List<FamilySymbol>();
            lst.Add(symbol);
        }

        foreach (var lst in byMark.Values)
            lst.Sort((a, b) =>
            {
                int c = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.FamilyName ?? "", b.FamilyName ?? "");
                return c != 0 ? c : NaturalStringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? "");
            });
        return byMark;
    }

    /// <summary>Ids of the symbols of a category that have at least one instance in the model.</summary>
    private static HashSet<ElementId> PlacedSymbolIds(Document doc, BuiltInCategory cat)
    {
        var ids = new HashSet<ElementId>();
        var instances = new FilteredElementCollector(doc)
            .OfCategory(cat)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();
        foreach (var fi in instances)
            ids.Add(fi.GetTypeId());
        return ids;
    }

    private static SpecField BuildField(FieldDef def, List<FamilySymbol> symbols, List<SpecTypeRef> types)
    {
        var field = new SpecField(def);

        var prms = symbols.Select(s => Resolve(s, def)).ToList();

        // n/a dominates: absent on any symbol, or symbols disagree on storage type.
        if (prms.Any(p => p == null))
        {
            field.NaDetail = SpecTypeBreakdown.Missing(types.Where((_, i) => prms[i] == null));
            field.IsNa = true;
            return field;
        }
        var storageTypes = prms.Select(p => p.StorageType).Distinct().ToList();
        if (storageTypes.Count > 1)
        {
            field.NaDetail = SpecTypeBreakdown.StorageMismatch(
                types.Zip(prms, (t, p) => (t, StorageKindName(p.StorageType))));
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
            field.VariesDetail = SpecTypeBreakdown.Varies(types.Zip(values, (t, v) => (t, v)), field.ValueKind);
            field.IsVaries = true;       // leave Value empty; placeholder ⟨varies⟩
        }
        else
        {
            field.SetInitialValue(values[0]);
        }
        return field;
    }

    private static string StorageKindName(StorageType st) => st switch
    {
        StorageType.String => "Text",
        StorageType.Integer => "Integer / Yes-No",
        StorageType.Double => "Number",
        StorageType.ElementId => "Element",
        _ => st.ToString()
    };

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
