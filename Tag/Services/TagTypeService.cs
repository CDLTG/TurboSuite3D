using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Tag.Constants;

namespace TurboSuite.Tag.Services;

internal static class TagTypeService
{
    private static ElementId _cachedTagTypeId = ElementId.InvalidElementId;
    private static readonly Dictionary<string, ElementId> _cachedKeypadTagTypeIds = new();
    private static readonly Dictionary<string, ElementId> _cachedLinearTagTypeIds = new();
    private static string? _cachedDocumentPath;

    private static bool IsSameDocument(Document doc)
    {
        string currentPath = doc.PathName ?? doc.Title;
        if (_cachedDocumentPath == currentPath)
            return true;

        _cachedDocumentPath = currentPath;
        _cachedTagTypeId = ElementId.InvalidElementId;
        _cachedKeypadTagTypeIds.Clear();
        _cachedLinearTagTypeIds.Clear();
        return false;
    }

    public static FamilySymbol? GetTagType(Document doc)
    {
        if (IsSameDocument(doc) && _cachedTagTypeId != ElementId.InvalidElementId)
        {
            var cached = doc.GetElement(_cachedTagTypeId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingFixtureTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.TagFamilyName, StringComparison.OrdinalIgnoreCase));

        if (tagType != null)
            _cachedTagTypeId = tagType.Id;

        return tagType;
    }

    public static FamilySymbol? GetSwitchIdTagType(Document doc)
    {
        string cacheKey = "_switchId";

        if (IsSameDocument(doc) && _cachedKeypadTagTypeIds.TryGetValue(cacheKey, out var cachedId))
        {
            var cached = doc.GetElement(cachedId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingDeviceTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.SwitchIdTagFamilyName, StringComparison.OrdinalIgnoreCase));

        if (tagType != null)
            _cachedKeypadTagTypeIds[cacheKey] = tagType.Id;

        return tagType;
    }

    public static FamilySymbol? GetKeypadTagType(Document doc, string? typeName = null)
    {
        string cacheKey = typeName ?? string.Empty;

        if (IsSameDocument(doc) && _cachedKeypadTagTypeIds.TryGetValue(cacheKey, out var cachedId))
        {
            var cached = doc.GetElement(cachedId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingDeviceTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.KeypadTagFamilyName, StringComparison.OrdinalIgnoreCase)
                               && (typeName == null || string.Equals(fs.Name, typeName, StringComparison.OrdinalIgnoreCase)));

        if (tagType != null)
            _cachedKeypadTagTypeIds[cacheKey] = tagType.Id;

        return tagType;
    }

    public static FamilySymbol? GetLinearTagType(Document doc, string typeName)
    {
        if (IsSameDocument(doc) && _cachedLinearTagTypeIds.TryGetValue(typeName, out var cachedId))
        {
            var cached = doc.GetElement(cachedId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_LightingFixtureTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.Equals(fs.FamilyName, TagConstants.LinearTagFamilyName, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(fs.Name, typeName, StringComparison.OrdinalIgnoreCase));

        if (tagType != null)
            _cachedLinearTagTypeIds[typeName] = tagType.Id;

        return tagType;
    }
}
