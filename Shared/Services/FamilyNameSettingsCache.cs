using Autodesk.Revit.DB;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Per-document in-memory cache for <see cref="FamilyNameSettingsStorageService"/> reads.
/// Invalidates when the document hash changes. Call <see cref="Invalidate"/> after a save.
/// </summary>
public static class FamilyNameSettingsCache
{
    private static FamilyNameSettings? _cached;
    private static int _docHashCode;

    public static FamilyNameSettings Get(Document doc)
    {
        int hash = doc.GetHashCode();
        if (_cached != null && _docHashCode == hash)
            return _cached;

        _cached = FamilyNameSettingsStorageService.Load(doc)
                  ?? FamilyNameSettings.CreateDefaults();
        _docHashCode = hash;
        return _cached;
    }

    public static void Invalidate() => _cached = null;
}
