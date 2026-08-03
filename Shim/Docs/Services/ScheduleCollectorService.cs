using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class ScheduleCollectorService
{
    public static List<ScheduleFixtureModel> Collect(Document doc)
    {
        var symbolIds = new HashSet<ElementId>();
        var fixtures = new List<ScheduleFixtureModel>();

        var instances = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_LightingFixtures)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in instances)
        {
            var symbol = fi.Symbol;
            if (symbol == null || !symbolIds.Add(symbol.Id)) continue;

            var tmParam = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            string typeMark = (tmParam is { HasValue: true }) ? tmParam.AsString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(typeMark)) continue;

            // Catalog Number1–6
            var catParts = new List<string>();
            for (int c = 1; c <= 6; c++)
            {
                string val = ReadStringParam(symbol, $"Catalog Number{c}");
                if (!string.IsNullOrWhiteSpace(val))
                    // Length tokens are per-instance; the type-level schedule has no single
                    // length, so show a generic [*] placeholder to the consumer.
                    catParts.Add(CatalogLengthTokenResolver.StripTokensToPlaceholder(val.Trim()));
            }

            // Schedule Note1–6
            var notes = new List<string>();
            for (int n = 1; n <= 6; n++)
            {
                string val = ReadStringParam(symbol, $"Schedule Notes{n}");
                if (!string.IsNullOrWhiteSpace(val)) notes.Add(val.Trim());
            }

            bool isLinear = !IsZeroDouble(fi, "Linear Power");
            string wattsVal = IsZeroDouble(symbol, "Power") ? "" : ReadParam(symbol, "Power").Replace(" VA", isLinear ? " W/ft" : " W");
            string lumensVal = IsZeroDouble(symbol, "Lumens") ? "" : ReadParam(symbol, "Lumens");
            if (lumensVal != "" && isLinear)
                lumensVal = lumensVal.Contains(" lm") ? lumensVal.Replace(" lm", " lm/ft") : lumensVal + " lm/ft";

            fixtures.Add(new ScheduleFixtureModel
            {
                TypeMark = typeMark,
                FamilyName = symbol.FamilyName,
                Classification = ReadStringParam(symbol, "Classification"),
                CatalogNumber = string.Join(" | ", catParts),
                Manufacturer = ReadBuiltIn(symbol, BuiltInParameter.ALL_MODEL_MANUFACTURER),
                Description1 = ReadBuiltIn(symbol, BuiltInParameter.ALL_MODEL_DESCRIPTION),
                Description2 = ReadStringParam(symbol, "Description2"),
                Finish = ConcatFinish(symbol),
                Listings = ReadStringParam(symbol, "Listings and Ratings"),
                Mounting = ReadStringParam(symbol, "Mounting"),
                Dimming = ReadStringParam(symbol, "Dimming Protocol"),
                Watts = wattsVal,
                Volts = (wattsVal == "" && lumensVal == "") ? "" : ReadParam(symbol, "Voltage"),
                Lumens = lumensVal,
                CCT = ReadParam(symbol, "Correlated Color Temperature (CCT)"),
                CRI = ReadParam(symbol, "Color Rendering Index (CRI)"),
                ScheduleNotes = notes.ToArray()
            });
        }

        fixtures.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase));
        return fixtures;
    }

    private static bool IsZeroDouble(Element element, string name)
    {
        var param = element.LookupParameter(name);
        if (param is not { HasValue: true }) return true;
        if (param.StorageType != StorageType.Double) return false;
        return System.Math.Abs(param.AsDouble()) < 1e-9;
    }

    private static string Sanitize(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            value = value.Replace("\r\n", ", ").Replace("\n", ", ").Replace("\r", ", ");
        return value.Trim();
    }

    private static string ConcatFinish(FamilySymbol symbol)
    {
        string f1 = ReadStringParam(symbol, "Finish1");
        string f2 = ReadStringParam(symbol, "Finish2");
        if (!string.IsNullOrWhiteSpace(f1) && !string.IsNullOrWhiteSpace(f2))
            return $"{f1} {f2}";
        return string.IsNullOrWhiteSpace(f1) ? f2 : f1;
    }

    private static string ReadBuiltIn(FamilySymbol symbol, BuiltInParameter bip)
    {
        var param = symbol.get_Parameter(bip);
        if (param is not { HasValue: true }) return string.Empty;
        return param.AsString() ?? string.Empty;
    }

    private static string ReadStringParam(FamilySymbol symbol, string name)
    {
        var param = symbol.LookupParameter(name);
        if (param is not { HasValue: true }) return string.Empty;
        return Sanitize(param.AsString() ?? string.Empty);
    }

    private static string ReadParam(FamilySymbol symbol, string name)
    {
        var param = symbol.LookupParameter(name);
        if (param is not { HasValue: true }) return string.Empty;

        return Sanitize(param.StorageType switch
        {
            StorageType.String => param.AsString() ?? string.Empty,
            StorageType.Integer => param.AsInteger().ToString(),
            StorageType.Double => param.AsValueString() ?? param.AsDouble().ToString("F2"),
            StorageType.ElementId => param.AsValueString() ?? string.Empty,
            _ => string.Empty,
        });
    }
}
