using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Mask.Services;

/// <summary>
/// Resolves or extracts the nested Generic Annotation sub-family ("stamp") from a fixture family.
/// Stamps are loaded into the project as Stamp_&lt;FixtureFamilyName&gt; so they can be placed at the
/// view level on top of a masking region, preserving the visible fixture graphics.
///
/// EditFamily must NOT be called inside a Transaction. Call ResolveStamp before opening the
/// placement transaction.
/// </summary>
internal sealed class StampFamilyService
{
    private const string StampPrefix = "Stamp_";

    private readonly Document _project;
    private readonly Dictionary<string, FamilySymbol?> _cache = new();

    public StampFamilyService(Document project)
    {
        _project = project;
    }

    /// <summary>
    /// Returns the FamilySymbol of the stamp family for the given fixture family, extracting it
    /// from the source family on first encounter and loading it into the project. Returns null if
    /// the fixture family has no nested Generic Annotation. Failure reasons are appended to
    /// <paramref name="failures"/>.
    /// </summary>
    public FamilySymbol? ResolveStamp(Family fixtureFamily, List<string> failures)
    {
        string stampName = StampPrefix + fixtureFamily.Name;
        if (_cache.TryGetValue(stampName, out var cached))
            return cached;

        FamilySymbol? symbol = FindExistingStamp(stampName);
        if (symbol != null)
        {
            _cache[stampName] = symbol;
            return symbol;
        }

        try
        {
            symbol = ExtractAndLoadStamp(fixtureFamily, stampName, failures);
        }
        catch (Exception ex)
        {
            failures.Add($"{fixtureFamily.Name}: threw: {ex.Message}");
            symbol = null;
        }

        _cache[stampName] = symbol;
        return symbol;
    }

    private FamilySymbol? FindExistingStamp(string stampName)
    {
        var family = new FilteredElementCollector(_project)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(f => f.Name == stampName);
        if (family == null) return null;

        var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
        if (symbolId == null || symbolId == ElementId.InvalidElementId) return null;

        return _project.GetElement(symbolId) as FamilySymbol;
    }

    private FamilySymbol? ExtractAndLoadStamp(Family fixtureFamily, string stampName, List<string> failures)
    {
        Document? fixtureDoc = null;
        Document? annotationDoc = null;

        try
        {
            fixtureDoc = _project.EditFamily(fixtureFamily);
            if (fixtureDoc == null) { failures.Add($"{fixtureFamily.Name}: EditFamily returned null"); return null; }

            var annotationGenericCategoryId = new ElementId(BuiltInCategory.OST_GenericAnnotation);
            var nestedFamily = new FilteredElementCollector(fixtureDoc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.FamilyCategory?.Id == annotationGenericCategoryId);

            if (nestedFamily == null)
            {
                failures.Add($"{fixtureFamily.Name}: no Generic Annotation nested family");
                return null;
            }

            // Guard against clobbering an unrelated project family with the same name as the nested
            // family (typically "Symbol"). LoadFamily would overwrite it via OnFamilyFound.
            string nestedName = nestedFamily.Name;
            bool collision = new FilteredElementCollector(_project)
                .OfClass(typeof(Family)).Cast<Family>()
                .Any(f => f.Name == nestedName);
            if (collision)
            {
                failures.Add($"{fixtureFamily.Name}: project already has a family named '{nestedName}'. Rename or remove it, then re-run TurboMask.");
                return null;
            }

            annotationDoc = fixtureDoc.EditFamily(nestedFamily);
            if (annotationDoc == null) { failures.Add($"{fixtureFamily.Name}: nested EditFamily returned null"); return null; }

            // Load the family directly from the editor document into the project — no SaveAs,
            // no temp file, no round-trip. Initially loaded under its original name (e.g., "Symbol").
            var loadedFamily = annotationDoc.LoadFamily(_project, new SilentFamilyLoadOptions());
            if (loadedFamily == null)
            {
                failures.Add($"{fixtureFamily.Name}: in-memory LoadFamily returned null");
                return null;
            }

            // Rename the loaded Family element in the project to the desired stamp name.
            try
            {
                using var renameTx = new Transaction(_project, "Rename Stamp Family");
                renameTx.Start();
                loadedFamily.Name = stampName;
                renameTx.Commit();
            }
            catch (Exception ex)
            {
                failures.Add($"{fixtureFamily.Name}: rename to '{stampName}' threw: {ex.Message}");
            }

            var symbolId = loadedFamily.GetFamilySymbolIds().FirstOrDefault();
            if (symbolId == null || symbolId == ElementId.InvalidElementId)
            {
                failures.Add($"{fixtureFamily.Name}: loaded '{stampName}' has no FamilySymbol");
                return null;
            }

            return _project.GetElement(symbolId) as FamilySymbol;
        }
        finally
        {
            try { annotationDoc?.Close(false); } catch { }
            try { fixtureDoc?.Close(false); } catch { }
        }
    }
}
