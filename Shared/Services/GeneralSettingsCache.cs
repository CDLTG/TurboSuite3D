using Autodesk.Revit.DB;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class GeneralSettingsCache
{
    private static GeneralSettings? _cached;
    private static int _docHashCode;

    public static GeneralSettings Get(Document doc)
    {
        int hash = doc.GetHashCode();
        if (_cached != null && _docHashCode == hash)
            return _cached;

        _cached = GeneralSettingsStorageService.Load(doc)
                  ?? GeneralSettings.CreateDefaults();
        _docHashCode = hash;
        return _cached;
    }

    public static void Invalidate() => _cached = null;
}
