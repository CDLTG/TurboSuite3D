using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace TurboSuite.Shared.Filters;

/// <summary>
/// Selection filter that accepts both Lighting Fixture and Electrical Fixture elements.
/// </summary>
public class FixtureSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem is FamilyInstance fi &&
               (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures);
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
