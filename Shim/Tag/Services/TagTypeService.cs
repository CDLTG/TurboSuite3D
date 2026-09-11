using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Tag.Services;

internal static class TagTypeService
{
    private static ElementId _cachedTagTypeId = ElementId.InvalidElementId;
    private static readonly Dictionary<string, ElementId> _cachedKeypadTagTypeIds = new();
    private static readonly Dictionary<string, ElementId> _cachedLinearTagTypeIds = new();
    private static readonly Dictionary<string, ElementId> _cachedCombinedLinearTagTypeIds = new();
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
        _cachedCombinedLinearTagTypeIds.Clear();
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

        var tagType = ParameterHelper.FindByRole(
            doc, BuiltInCategory.OST_LightingFixtureTags, Roles.FixtureTypeTag);

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

        var tagType = ParameterHelper.FindByRole(
            doc, BuiltInCategory.OST_LightingDeviceTags, Roles.SwitchIdTag);

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

        var tagType = ParameterHelper.FindByRole(
            doc, BuiltInCategory.OST_LightingDeviceTags, Roles.KeypadTag, typeName);

        if (tagType != null)
            _cachedKeypadTagTypeIds[cacheKey] = tagType.Id;

        return tagType;
    }

    public static FamilySymbol? GetCombinedLinearTagType(Document doc, string typeName)
    {
        if (IsSameDocument(doc) && _cachedCombinedLinearTagTypeIds.TryGetValue(typeName, out var cachedId))
        {
            var cached = doc.GetElement(cachedId) as FamilySymbol;
            if (cached != null && cached.IsValidObject)
                return cached;
        }

        var tagType = ParameterHelper.FindByRole(
            doc, BuiltInCategory.OST_LightingFixtureTags, Roles.RunLengthTag, typeName);

        if (tagType != null)
            _cachedCombinedLinearTagTypeIds[typeName] = tagType.Id;

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

        var tagType = ParameterHelper.FindByRole(
            doc, BuiltInCategory.OST_LightingFixtureTags, Roles.LinearTag, typeName);

        if (tagType != null)
            _cachedLinearTagTypeIds[typeName] = tagType.Id;

        return tagType;
    }
}
