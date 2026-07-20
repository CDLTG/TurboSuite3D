using System;

namespace TurboSuite.Name;

/// <summary>
/// Pure link-scope matching for TurboName's per-purpose CAD scopes (room-name / ceiling-height / region-gen
/// source) and for the <c>file|layer</c>-qualified region-gen layer entries. Kept Revit-free in Core so the
/// disambiguation that kills TurboName-9 (a plan + RCP sharing a room-name layer double-seeding each room) is
/// pinned by unit tests. The shim extractors resolve live subcategory ids and DWG paths, then call these.
/// </summary>
public static class CadLinkScope
{
    /// <summary>
    /// Does <paramref name="scope"/> include the given DWG? Blank scope ⇒ every link. Otherwise a
    /// case-insensitive whole-filename match (both sides trimmed). A blank <paramref name="dwgFileName"/>
    /// only matches a blank scope.
    /// </summary>
    public static bool Includes(string? scope, string? dwgFileName)
    {
        // net48's IsNullOrWhiteSpace lacks the [NotNullWhen(false)] annotation net8 carries, so the null-
        // forgiving '!' is needed to keep the shared Core warning-clean on both target frameworks.
        if (string.IsNullOrWhiteSpace(scope)) return true;
        if (string.IsNullOrWhiteSpace(dwgFileName)) return false;
        return string.Equals(scope!.Trim(), dwgFileName!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Split a region-gen layer entry into its owning file and layer. <c>"Floor Plan.dwg|WALL_INTR"</c> ⇒
    /// (<c>"Floor Plan.dwg"</c>, <c>"WALL_INTR"</c>); a bare <c>"WALL_INTR"</c> (no <c>'|'</c>) ⇒
    /// (<c>null</c>, <c>"WALL_INTR"</c>) = legacy, matched by name under the SourceLinkName scope. An empty
    /// file part (<c>"|WALL_INTR"</c>) is also treated as legacy. Both parts are trimmed.
    /// </summary>
    public static (string? File, string Layer) ParseScopedLayer(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return (null, "");
        int bar = entry!.IndexOf('|');
        if (bar < 0) return (null, entry.Trim());
        string file = entry.Substring(0, bar).Trim();
        string layer = entry.Substring(bar + 1).Trim();
        return (file.Length == 0 ? null : file, layer);
    }

    /// <summary>
    /// Does a parsed region-gen entry (<paramref name="entryFile"/>, <paramref name="entryLayer"/>) match a
    /// CAD entity on <paramref name="layerName"/> in the DWG <paramref name="dwgFileName"/>? A qualified entry
    /// matches only its own file; a bare (null-file) legacy entry matches any DWG the
    /// <paramref name="legacyScope"/> (SourceLinkName) includes. Layer names compared case-insensitively.
    /// </summary>
    public static bool MatchesLayer(string? entryFile, string entryLayer,
        string layerName, string? legacyScope, string? dwgFileName)
    {
        if (!string.Equals(entryLayer, layerName, StringComparison.OrdinalIgnoreCase)) return false;
        return entryFile == null
            ? Includes(legacyScope, dwgFileName)
            : string.Equals(entryFile, dwgFileName, StringComparison.OrdinalIgnoreCase);
    }
}
