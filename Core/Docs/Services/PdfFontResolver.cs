using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Fonts;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Font resolver for PDFsharp 6.x core (netstandard2.0/net8/net10). Unlike PdfSharpCore, the
/// plain PDFsharp core package does NOT auto-resolve system fonts — with no resolver set, the
/// first <c>new XFont(...)</c> throws. The suite uses exactly two families, "Segoe UI" and
/// "Segoe UI Light", only ever Regular or Bold (no italic), so this is pure file-path mapping
/// out of <c>%WINDIR%\Fonts</c>; PDFsharp does all glyph/metric work from the TTF bytes.
/// Windows-only is a simplification: these renderers run inside Revit on a Windows workstation
/// where Segoe is always present.
/// </summary>
internal sealed class PdfFontResolver : IFontResolver
{
    private static readonly string FontDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // face-key -> filename in %WINDIR%\Fonts. Verified against C:\Windows\Fonts (2026-08-24):
    // segoeuil.ttf is Light; segoeuisl.ttf (Semilight) is the trap and is deliberately NOT used.
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["SegoeUI"] = "segoeui.ttf",
        ["SegoeUI#b"] = "segoeuib.ttf",
        ["SegoeUILight"] = "segoeuil.ttf",
    };

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    private static readonly object RegisterLock = new();

    /// <summary>
    /// Idempotently install this resolver before the first render. Each PDF service calls this
    /// from its static constructor, so it is guaranteed to run before any XFont/XGraphics touch.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (GlobalFontSettings.FontResolver != null) return;
        lock (RegisterLock)
        {
            GlobalFontSettings.FontResolver ??= new PdfFontResolver();
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var f = familyName.Trim();
        // Light + Bold is contradictory; ignore bold. Italic never occurs in the codebase.
        if (f.Equals("Segoe UI Light", StringComparison.OrdinalIgnoreCase))
            return new FontResolverInfo("SegoeUILight");
        // "Segoe UI" and any unexpected family degrade to regular/bold Segoe UI — never throw.
        return new FontResolverInfo(bold ? "SegoeUI#b" : "SegoeUI");
    }

    public byte[]? GetFont(string faceName) =>
        Cache.GetOrAdd(faceName, key =>
            File.ReadAllBytes(Path.Combine(
                FontDir, Files.TryGetValue(key, out var name) ? name : "segoeui.ttf")));
}
