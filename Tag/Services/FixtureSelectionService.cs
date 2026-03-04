using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

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

    public static bool IsLineBased(FamilyInstance fixture)
    {
        return GeometryHelper.IsLineBasedFixture(fixture);
    }

    public static bool IsOnVerticalFace(FamilyInstance fixture)
    {
        return GeometryHelper.IsOnVerticalFace(fixture);
    }
}
