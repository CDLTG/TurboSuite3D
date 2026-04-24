using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class CountsCollectorService
{
    private static readonly BuiltInCategory[] Categories =
    [
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_ElectricalFixtures,
    ];

    public static List<CountsFixtureModel> Collect(Document doc)
    {
        // Key: Type Mark → aggregated data
        var byTypeMark = new Dictionary<string, CountsFixtureModel>(StringComparer.OrdinalIgnoreCase);
        var seenSymbols = new HashSet<ElementId>();

        foreach (var category in Categories)
        {
            var instances = new FilteredElementCollector(doc)
                .OfCategory(category)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in instances)
            {
                var symbol = fi.Symbol;
                if (symbol == null) continue;

                var tmParam = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                string typeMark = (tmParam is { HasValue: true }) ? tmParam.AsString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(typeMark)) continue;

                if (!byTypeMark.TryGetValue(typeMark, out var model))
                {
                    model = new CountsFixtureModel { TypeMark = typeMark };
                    byTypeMark[typeMark] = model;
                }

                // Count every instance
                model.Count++;

                // Sum Linear Length from instances
                var llParam = fi.LookupParameter("Linear Length");
                if (llParam is { HasValue: true, StorageType: StorageType.Double })
                    model.LinearLength += llParam.AsDouble();

                // Read type-level parameters once per symbol
                if (!seenSymbols.Add(symbol.Id)) continue;

                var mfrParam = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER);
                if (mfrParam is { HasValue: true })
                    model.Manufacturer = mfrParam.AsString() ?? string.Empty;

                for (int c = 0; c < 6; c++)
                {
                    var catParam = symbol.LookupParameter($"Catalog Number{c + 1}");
                    if (catParam is { HasValue: true })
                        model.CatalogNumbers[c] = catParam.AsString()?.Trim() ?? string.Empty;
                }

                var rlParam = symbol.LookupParameter("Reel Length");
                if (rlParam is { HasValue: true, StorageType: StorageType.Double })
                    model.ReelLength = rlParam.AsDouble();

                var clParam = symbol.LookupParameter("Channel Length");
                if (clParam is { HasValue: true, StorageType: StorageType.Double })
                    model.ChannelLength = clParam.AsDouble();

                for (int n = 0; n < 6; n++)
                {
                    var noteParam = symbol.LookupParameter($"Schedule Notes{n + 1}");
                    if (noteParam is { HasValue: true })
                        model.Notes[n] = noteParam.AsString()?.Trim() ?? string.Empty;
                }
            }
        }

        var result = byTypeMark.Values.ToList();
        result.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
