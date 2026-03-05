using Autodesk.Revit.DB;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

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
