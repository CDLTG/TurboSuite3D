using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace TurboSuite.Shared.Filters;

/// <summary>
/// Selection filter that only accepts lighting fixture elements.
/// </summary>
public class LightingFixtureSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem is FamilyInstance fi &&
               (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures);
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
