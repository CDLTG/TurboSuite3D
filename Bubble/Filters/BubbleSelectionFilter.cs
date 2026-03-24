using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace TurboSuite.Bubble.Filters;

/// <summary>
/// Selection filter that accepts lighting fixture tags and electrical fixture instances.
/// </summary>
internal class BubbleSelectionFilter : ISelectionFilter
{
    private static readonly long LightingFixturesCategoryId = (long)BuiltInCategory.OST_LightingFixtures;

    public bool AllowElement(Element elem)
    {
        if (elem is IndependentTag tag)
        {
            foreach (var id in tag.GetTaggedLocalElementIds())
            {
                var tagged = elem.Document.GetElement(id);
                if (tagged?.Category?.Id.Value == LightingFixturesCategoryId)
                    return true;
            }
            return false;
        }

        if (elem is FamilyInstance fi &&
            fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures)
            return true;

        return false;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
