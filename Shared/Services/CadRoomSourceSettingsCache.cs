using Autodesk.Revit.DB;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class CadRoomSourceSettingsCache
{
    private static CadRoomSourceSettings? _cached;
    private static int _docHashCode;

    public static CadRoomSourceSettings Get(Document doc)
    {
        int hash = doc.GetHashCode();
        if (_cached != null && _docHashCode == hash)
            return _cached;

        _cached = CadRoomSourceStorageService.Load(doc)
                  ?? CadRoomSourceSettings.CreateDefaults();
        _docHashCode = hash;
        return _cached;
    }

    public static void Invalidate() => _cached = null;
}
