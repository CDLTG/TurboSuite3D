using Autodesk.Revit.DB;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Per-document in-memory cache for <see cref="GeneralSettingsStorageService"/> reads.
/// Invalidates when the document changes. Call <see cref="Invalidate"/> after a save.
/// Keyed by <see cref="Document.PathName"/>, not <c>GetHashCode()</c> — identity hashes are
/// reused after GC and can collide, serving one project's settings to another (see CLAUDE.md).
/// </summary>
public static class GeneralSettingsCache
{
    private static GeneralSettings? _cached;
    private static string? _docPath;

    public static GeneralSettings Get(Document doc)
    {
        string path = doc.PathName;
        if (_cached != null && _docPath == path)
            return _cached;

        _cached = GeneralSettingsStorageService.Load(doc)
                  ?? GeneralSettings.CreateDefaults();
        _docPath = path;
        return _cached;
    }

    public static void Invalidate() => _cached = null;
}
