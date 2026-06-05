using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace TurboSuite.Shared.Filters;

/// <summary>
/// Selection filter that only accepts tags attached to lighting fixtures.
/// </summary>
public class LightingFixtureTagFilter : ISelectionFilter
{
    private static readonly long LightingFixturesCategoryId = (long)BuiltInCategory.OST_LightingFixtures;

    public bool AllowElement(Element elem)
    {
        if (elem is not IndependentTag tag) return false;

        foreach (var id in tag.GetTaggedLocalElementIds())
        {
            var taggedElem = elem.Document.GetElement(id);
            if (taggedElem?.Category?.Id.Value == LightingFixturesCategoryId)
                return true;
        }

        return false;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
