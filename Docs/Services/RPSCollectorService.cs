using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Docs.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Docs.Services;

public static class RPSCollectorService
{
    public static (List<RPSScheduleModel> scheduleItems, List<RPSInstanceModel> instances, List<FixtureSpecModel> cutSheetItems)
        Collect(Document doc)
    {
        var symbolIds = new HashSet<ElementId>();
        var scheduleItems = new List<RPSScheduleModel>();
        var instances = new List<RPSInstanceModel>();
        var cutSheetIds = new HashSet<ElementId>();
        var cutSheetItems = new List<FixtureSpecModel>();

        var elements = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_LightingDevices)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in elements)
        {
            var symbol = fi.Symbol;
            if (symbol == null) continue;

            // Valid driver check: Power > 0, Sub-Driver Power > 0, evenly divisible
            double power = ParameterHelper.GetDriverPower(symbol);
            double subPower = ParameterHelper.GetSubDriverPower(symbol);
            if (power <= 0 || subPower <= 0) continue;
            if (Math.Abs(power % subPower) >= 0.01) continue;

            // --- Instance data (every valid device) ---
            string switchId = ParameterHelper.GetSwitchID(fi);
            string typeMark = ReadBuiltIn(symbol, BuiltInParameter.ALL_MODEL_TYPE_MARK);

            // Get circuit info
            var circuit = fi.MEPModel?.GetElectricalSystems()?.FirstOrDefault();
            string loadName = circuit != null ? ParameterHelper.GetLoadName(circuit) : string.Empty;
            string circuitNumber = circuit != null ? ParameterHelper.GetCircuitNumber(circuit) : string.Empty;

            string instanceCatalog = BuildCatalogNumber(symbol);

            if (!string.IsNullOrWhiteSpace(switchId))
            {
                instances.Add(new RPSInstanceModel
                {
                    SwitchID = switchId,
                    TypeMark = typeMark,
                    CatalogNumber = instanceCatalog,
                    LoadName = loadName,
                    CircuitNumber = circuitNumber
                });
            }

            // --- Schedule data (unique symbols only) ---
            if (!string.IsNullOrWhiteSpace(typeMark) && symbolIds.Add(symbol.Id))
            {
                // Schedule Notes1–6
                var notes = new List<string>();
                for (int n = 1; n <= 6; n++)
                {
                    string val = ReadStringParam(symbol, $"Schedule Notes{n}");
                    if (!string.IsNullOrWhiteSpace(val)) notes.Add(val.Trim());
                }

                int maxFixtures = ParameterHelper.GetMaximumFixtures(symbol);

                scheduleItems.Add(new RPSScheduleModel
                {
                    TypeMark = typeMark,
                    FamilyName = symbol.FamilyName,
                    Classification = ReadStringParam(symbol, "Classification"),
                    CatalogNumber = instanceCatalog,
                    Manufacturer = ReadBuiltIn(symbol, BuiltInParameter.ALL_MODEL_MANUFACTURER),
                    Description1 = ReadBuiltIn(symbol, BuiltInParameter.ALL_MODEL_DESCRIPTION),
                    Description2 = ReadStringParam(symbol, "Description2"),
                    Power = FormatWatts(power),
                    SubDriverPower = FormatWatts(subPower),
                    MaxFixtures = maxFixtures > 0 ? maxFixtures.ToString() : string.Empty,
                    Dimming = ReadStringParam(symbol, "Dimming Protocol"),
                    Voltage = ReadParam(symbol, "Voltage"),
                    ScheduleNotes = notes.ToArray()
                });

                // Cut sheet data
                if (cutSheetIds.Add(symbol.Id))
                {
                    string dataSheetUrl = ReadStringParam(symbol, "Data Sheet URL");
                    cutSheetItems.Add(new FixtureSpecModel
                    {
                        TypeMark = typeMark,
                        FamilyName = symbol.FamilyName,
                        DataSheetUrl = dataSheetUrl,
                        CatalogNumber = instanceCatalog,
                        SymbolId = symbol.Id
                    });
                }
            }
        }

        // Sort schedule items by TypeMark
        scheduleItems.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase));

        // Sort instances by SwitchID (numeric-aware)
        instances.Sort((a, b) =>
        {
            bool aNum = int.TryParse(a.SwitchID, out int aVal);
            bool bNum = int.TryParse(b.SwitchID, out int bVal);
            if (aNum && bNum) return aVal.CompareTo(bVal);
            if (aNum) return -1;
            if (bNum) return 1;
            return string.Compare(a.SwitchID, b.SwitchID, StringComparison.OrdinalIgnoreCase);
        });

        // Sort cut sheets by TypeMark
        cutSheetItems.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase));

        return (scheduleItems, instances, cutSheetItems);
    }

    private static string BuildCatalogNumber(FamilySymbol symbol)
    {
        var parts = new List<string>();
        for (int c = 1; c <= 6; c++)
        {
            string val = ReadStringParam(symbol, $"Catalog Number{c}");
            if (!string.IsNullOrWhiteSpace(val)) parts.Add(val.Trim());
        }
        return string.Join(" | ", parts);
    }

    private static string FormatWatts(double watts)
    {
        if (watts <= 0) return string.Empty;
        return watts % 1 == 0 ? $"{(int)watts} W" : $"{watts:F1} W";
    }

    private static string Sanitize(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            value = value.Replace("\r\n", ", ").Replace("\n", ", ").Replace("\r", ", ");
        return value.Trim();
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
