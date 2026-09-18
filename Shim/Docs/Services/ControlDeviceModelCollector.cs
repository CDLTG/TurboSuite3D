using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Collects the distinct <c>Model</c> values of the placed keypad and hybrid-repeater families, for the
/// Cut Sheets Control Package mode. Keypads and repeaters are the one control-part family whose cutsheet
/// is keyed by <c>Model</c> (not part number) — every color/button/engraving SKU of a family shares one
/// <c>Model</c>, 1:1 with the cutsheet — so cut sheets need only the distinct Models, not the BOM tally's
/// per-slot catalog rows. Same category + role filters TurboZones uses to count these devices.
/// </summary>
public static class ControlDeviceModelCollector
{
    public static (List<string> KeypadModels, List<string> RepeaterModels) Collect(Document doc)
    {
        return (
            DistinctModels(doc, BuiltInCategory.OST_LightingDevices, Roles.Keypad),
            DistinctModels(doc, BuiltInCategory.OST_ElectricalFixtures, Roles.HybridRepeater));
    }

    private static List<string> DistinctModels(Document doc, BuiltInCategory category, string role)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(category)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => ParameterHelper.GetRole(fi) == role)
            // Model is a type param (Identity Data); read it off the symbol, same as NumberCollectorService.
            .Select(fi => fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString()?.Trim())
            // Drop blanks: a Model-less family has no key to resolve and an empty-label row helps no one.
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
