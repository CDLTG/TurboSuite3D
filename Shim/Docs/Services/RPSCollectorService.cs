using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Docs.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Docs.Services;

public static class RPSCollectorService
{
    /// <summary>
    /// Build the driver/sub-driver breakdown for the Power Supplies "Driver Breakdown" output.
    /// Reuses the exact TurboRPS dashboard pipeline (<see cref="RpsCircuitDataBuilder"/>:
    /// collect → recommend → classify), projecting each circuit that has a real driver match
    /// into a Revit-free <see cref="RPSBreakdownModel"/>. Circuits with no matching driver (or
    /// DMX-decoder-managed circuits, which get no driver recommendation) are omitted.
    /// </summary>
    public static List<RPSBreakdownModel> CollectBreakdown(Document doc)
    {
        var result = new List<RPSBreakdownModel>();

        foreach (var c in RpsCircuitDataBuilder.Build(doc))
        {
            var reco = c.Recommendation;
            if (reco is not { HasMatch: true } || reco.SubDriverAssignments.Count == 0)
                continue;

            // Group fixtures exactly as the TurboRPS detail pane does (type + comments + length).
            var fixtures = c.Fixtures
                .GroupBy(f => new { f.TypeMark, f.Comments, LinearLength = Math.Round(f.LinearLength, 4) })
                .Select(g => new BreakdownFixture
                {
                    Quantity = g.Count(),
                    TypeMark = g.Key.TypeMark ?? string.Empty,
                    Comments = g.Key.Comments ?? string.Empty,
                    LinearLength = g.Key.LinearLength
                })
                .OrderBy(f => f.TypeMark, NaturalStringComparer.OrdinalIgnoreCase)
                .ToList();

            result.Add(new RPSBreakdownModel
            {
                CircuitNumber = c.CircuitNumber ?? string.Empty,
                LoadName = c.LoadName ?? string.Empty,
                SwitchIds = c.SwitchIds is { Count: > 0 } ? string.Join(", ", c.SwitchIds) : string.Empty,
                RecommendedType = c.RecommendedTypeName ?? string.Empty,
                DriverCount = c.RecommendedCount,
                TotalLoadWatts = c.RpsLoadWatts,
                SubDrivers = reco.SubDriverAssignments,
                Fixtures = fixtures
            });
        }

        result.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.CircuitNumber, b.CircuitNumber));
        return result;
    }

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

        // Natural sort so RPS-2 sorts before RPS-10 and X09 sorts before X100.
        scheduleItems.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.TypeMark, b.TypeMark));
        instances.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.SwitchID, b.SwitchID));
        cutSheetItems.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.TypeMark, b.TypeMark));

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
