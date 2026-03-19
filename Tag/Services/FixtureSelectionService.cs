using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Tag.Services;

internal static class FixtureSelectionService
{
    public static List<FamilyInstance> GetSelectedLightingFixtures(Document doc, ICollection<ElementId> selectedIds)
    {
        if (selectedIds.Count == 0)
            return new List<FamilyInstance>();

        var lightingCategoryId = new ElementId(BuiltInCategory.OST_LightingFixtures);
        var fixtures = new List<FamilyInstance>(selectedIds.Count);

        foreach (ElementId id in selectedIds)
        {
            if (doc.GetElement(id) is FamilyInstance fi &&
                fi.Category?.Id == lightingCategoryId)
            {
                fixtures.Add(fi);
            }
        }

        return fixtures;
    }

    public static List<FamilyInstance> GetSelectedPowerSupplies(Document doc, ICollection<ElementId> selectedIds)
    {
        if (selectedIds.Count == 0)
            return new List<FamilyInstance>();

        var lightingDeviceCategoryId = new ElementId(BuiltInCategory.OST_LightingDevices);
        var powerSupplies = new List<FamilyInstance>();

        foreach (ElementId id in selectedIds)
        {
            if (doc.GetElement(id) is FamilyInstance fi &&
                fi.Category?.Id == lightingDeviceCategoryId &&
                fi.Symbol?.LookupParameter("Sub-Driver Power") != null)
            {
                powerSupplies.Add(fi);
            }
        }

        return powerSupplies;
    }

    public static List<FamilyInstance> GetSelectedKeypads(Document doc, ICollection<ElementId> selectedIds)
    {
        if (selectedIds.Count == 0)
            return new List<FamilyInstance>();

        var lightingDeviceCategoryId = new ElementId(BuiltInCategory.OST_LightingDevices);
        var keypads = new List<FamilyInstance>();

        foreach (ElementId id in selectedIds)
        {
            if (doc.GetElement(id) is FamilyInstance fi &&
                fi.Category?.Id == lightingDeviceCategoryId &&
                fi.Symbol.FamilyName.IndexOf("Keypad", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                keypads.Add(fi);
            }
        }

        return keypads;
    }
}
