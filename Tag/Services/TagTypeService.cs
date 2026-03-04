using System;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Tag.Constants;

namespace TurboSuite.Tag.Services;

internal static class TagTypeService
{
    private static ElementId _cachedTagTypeId = ElementId.InvalidElementId;
    private static string? _cachedDocumentPath;

    public static FamilySymbol? GetTagType(Document doc)
    {
        string currentPath = doc.PathName ?? doc.Title;

        if (_cachedTagTypeId != ElementId.InvalidElementId &&
            _cachedDocumentPath == currentPath)
        {
            var cached = doc.GetElement(_cachedTagTypeId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingFixtureTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.TAG_FAMILY_NAME, StringComparison.OrdinalIgnoreCase));

        if (tagType != null)
        {
            _cachedTagTypeId = tagType.Id;
            _cachedDocumentPath = currentPath;
        }

        return tagType;
    }

    public static FamilySymbol? GetLinearTagType(Document doc, string typeName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingFixtureTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.LINEAR_TAG_FAMILY_NAME, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(fs.Name, typeName, StringComparison.OrdinalIgnoreCase));
    }
}
