using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace TurboSuite.Bubble.Services;

/// <summary>
/// Service for finding tag types and wire types in the project.
/// </summary>
internal static class FixtureAnalysisService
{
    /// <summary>
    /// Finds a tag type by family name in Lighting Fixture tags.
    /// </summary>
    public static ElementId? FindTagType(Document doc, string familyName)
    {
        return FindTagType(doc, BuiltInCategory.OST_LightingFixtureTags, familyName);
    }

    /// <summary>
    /// Finds a specific tag type by family name and type name in Lighting Fixture tags.
    /// </summary>
    public static ElementId? FindTagType(Document doc, string familyName, string typeName)
    {
        return FindTagType(doc, BuiltInCategory.OST_LightingFixtureTags, familyName, typeName);
    }

    /// <summary>
    /// Finds a tag type by family name in the specified tag category.
    /// </summary>
    public static ElementId? FindTagType(Document doc, BuiltInCategory tagCategory, string familyName)
    {
        using var collector = new FilteredElementCollector(doc);
        foreach (var elem in collector
            .OfCategory(tagCategory)
            .OfClass(typeof(FamilySymbol)))
        {
            if (elem is FamilySymbol fs && fs.FamilyName == familyName)
                return fs.Id;
        }
        return null;
    }

    /// <summary>
    /// Finds a specific tag type by family name and type name in the specified tag category.
    /// </summary>
    public static ElementId? FindTagType(Document doc, BuiltInCategory tagCategory, string familyName, string typeName)
    {
        using var collector = new FilteredElementCollector(doc);
        foreach (var elem in collector
            .OfCategory(tagCategory)
            .OfClass(typeof(FamilySymbol)))
        {
            if (elem is FamilySymbol fs && fs.FamilyName == familyName && fs.Name == typeName)
                return fs.Id;
        }
        return null;
    }

    public static ElementId? FindFirstWireType(Document doc)
    {
        using var collector = new FilteredElementCollector(doc);
        var id = collector
            .OfCategory(BuiltInCategory.OST_Wire)
            .OfClass(typeof(WireType))
            .FirstElementId();

        return id != ElementId.InvalidElementId ? id : null;
    }
}
