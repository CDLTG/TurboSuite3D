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
/// Resolution is **per fixture type**, not per family: a multi-type fixture family (e.g. a receptacle
/// with duplex / duplex-hot / quadruplex / quadruplex-hot) type-swaps its nested annotation graphic
/// via a &lt;Family Type&gt;-valued family parameter, so each fixture type must land its own stamp
/// symbol type. The authoritative fixtureType → stampType map is read straight from the family
/// (FamilyManager.Types + FamilyType.AsElementId on the parameter whose per-type values resolve into
/// the nested annotation's own symbol set — never matched by parameter name, which is not stable).
/// LoadFamily already brings every nested symbol type into Stamp_&lt;Family&gt;, so the graphics are
/// present; this class only picks the right one for each placed type. Single-graphic families (one
/// nested symbol type) skip the mapping read entirely and stay as fast as before.
///
/// EditFamily must NOT be called inside a Transaction. Call ResolveStamp before opening the
/// placement transaction.
/// </summary>
internal sealed class StampFamilyService
{
    private const string StampPrefix = "Stamp_";

    private readonly Document _project;
    private readonly Dictionary<string, StampFamilyResolution?> _cache = new();
    private readonly HashSet<string> _reportedMisses = new();

    public StampFamilyService(Document project)
    {
        _project = project;
    }

    /// <summary>
    /// Returns the stamp FamilySymbol to place for the given fixture <paramref name="fixtureType"/>,
    /// extracting/loading the stamp family from the fixture's nested Generic Annotation on first
    /// encounter. Returns null if the fixture family has no nested Generic Annotation. Failure and
    /// fallback reasons are appended to <paramref name="failures"/>.
    /// </summary>
    public FamilySymbol? ResolveStamp(FamilySymbol fixtureType, List<string> failures)
    {
        var family = fixtureType.Family;
        if (family == null) return null;

        var res = GetOrBuildResolution(family, failures);
        if (res == null) return null;

        if (res.FixtureTypeToStampType.TryGetValue(fixtureType.Name, out var stampTypeName)
            && res.StampSymbolsByTypeName.TryGetValue(stampTypeName, out var mapped))
            return mapped;

        // More than one stamp graphic but this fixture type isn't mapped — surface it once and fall
        // back to the first symbol (the pre-fix behavior) so a mask still gets *a* footprint.
        if (res.StampSymbolsByTypeName.Count > 1)
        {
            string key = family.Name + "|" + fixtureType.Name;
            if (_reportedMisses.Add(key))
                failures.Add($"{family.Name} / type '{fixtureType.Name}': no stamp mapping found; used '{res.Fallback?.Name}'.");
        }
        return res.Fallback;
    }

    private StampFamilyResolution? GetOrBuildResolution(Family fixtureFamily, List<string> failures)
    {
        string stampName = StampPrefix + fixtureFamily.Name;
        if (_cache.TryGetValue(stampName, out var cached))
            return cached;

        StampFamilyResolution? res;
        try
        {
            res = BuildResolution(fixtureFamily, stampName, failures);
        }
        catch (Exception ex)
        {
            failures.Add($"{fixtureFamily.Name}: threw: {ex.Message}");
            res = null;
        }

        _cache[stampName] = res;
        return res;
    }

    private StampFamilyResolution? BuildResolution(Family fixtureFamily, string stampName, List<string> failures)
    {
        Dictionary<string, string>? mapFromExtraction = null;

        Family? stampFamily = FindExistingStampFamily(stampName);
        if (stampFamily == null)
        {
            stampFamily = ExtractAndLoadStamp(fixtureFamily, stampName, failures, out mapFromExtraction);
            if (stampFamily == null) return null;
        }

        var symbolsByName = new Dictionary<string, FamilySymbol>();
        FamilySymbol? fallback = null;
        foreach (var symId in stampFamily.GetFamilySymbolIds())
        {
            if (_project.GetElement(symId) is FamilySymbol fs)
            {
                symbolsByName[fs.Name] = fs;
                fallback ??= fs;
            }
        }

        if (fallback == null)
        {
            failures.Add($"{fixtureFamily.Name}: loaded '{stampName}' has no FamilySymbol");
            return null;
        }

        // One graphic ⇒ nothing to disambiguate; every type gets the sole symbol. This is the common
        // single-graphic family, and it skips the EditFamily-for-map read entirely.
        Dictionary<string, string> map;
        if (symbolsByName.Count <= 1)
            map = new Dictionary<string, string>();
        else
            map = mapFromExtraction ?? BuildMapViaEditFamily(fixtureFamily, failures);

        return new StampFamilyResolution
        {
            StampSymbolsByTypeName = symbolsByName,
            FixtureTypeToStampType = map,
            Fallback = fallback,
        };
    }

    private Family? FindExistingStampFamily(string stampName)
    {
        return new FilteredElementCollector(_project)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(f => f.Name == stampName);
    }

    private Family? ExtractAndLoadStamp(Family fixtureFamily, string stampName, List<string> failures,
        out Dictionary<string, string>? map)
    {
        map = null;
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

            // Read the fixtureType → nested-symbol-type map while the fixture family is still open —
            // free here, versus a second EditFamily on the reuse path. Nested symbol type names carry
            // over to the loaded stamp family unchanged (only the Family element was renamed), so the
            // map's values key straight into the stamp's symbols.
            map = BuildFixtureTypeMap(fixtureDoc, nestedFamily);

            return loadedFamily;
        }
        finally
        {
            try { annotationDoc?.Close(false); } catch { }
            try { fixtureDoc?.Close(false); } catch { }
        }
    }

    private Dictionary<string, string> BuildMapViaEditFamily(Family fixtureFamily, List<string> failures)
    {
        Document? fixtureDoc = null;
        try
        {
            fixtureDoc = _project.EditFamily(fixtureFamily);
            if (fixtureDoc == null)
            {
                failures.Add($"{fixtureFamily.Name}: EditFamily returned null (stamp mapping)");
                return new Dictionary<string, string>();
            }

            var annotationGenericCategoryId = new ElementId(BuiltInCategory.OST_GenericAnnotation);
            var nestedFamily = new FilteredElementCollector(fixtureDoc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.FamilyCategory?.Id == annotationGenericCategoryId);

            return nestedFamily == null
                ? new Dictionary<string, string>()
                : BuildFixtureTypeMap(fixtureDoc, nestedFamily);
        }
        catch (Exception ex)
        {
            failures.Add($"{fixtureFamily.Name}: stamp mapping threw: {ex.Message}");
            return new Dictionary<string, string>();
        }
        finally
        {
            try { fixtureDoc?.Close(false); } catch { }
        }
    }

    /// <summary>
    /// Builds fixtureTypeName → nestedAnnotationSymbolTypeName for a family open in
    /// <paramref name="familyDoc"/>. The driving parameter is discovered structurally, not by name:
    /// it is the ElementId-valued family parameter whose per-type value resolves to a symbol type of
    /// the nested annotation family — the only ElementId param that lands inside that set (a Type
    /// Image param, for instance, resolves to an image and is skipped). First qualifying param per
    /// type wins.
    /// </summary>
    private static Dictionary<string, string> BuildFixtureTypeMap(Document familyDoc, Family nestedFamily)
    {
        var map = new Dictionary<string, string>();

        var nestedSymbolNames = new Dictionary<ElementId, string>();
        foreach (var symId in nestedFamily.GetFamilySymbolIds())
        {
            if (familyDoc.GetElement(symId) is FamilySymbol fs)
                nestedSymbolNames[symId] = fs.Name;
        }
        if (nestedSymbolNames.Count == 0) return map;

        var manager = familyDoc.FamilyManager;
        var idParams = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(p => p.StorageType == StorageType.ElementId)
            .ToList();
        if (idParams.Count == 0) return map;

        foreach (FamilyType ft in manager.Types)
        {
            if (string.IsNullOrEmpty(ft.Name)) continue;
            foreach (var p in idParams)
            {
                if (!ft.HasValue(p)) continue;
                var eid = ft.AsElementId(p);
                if (eid != null && nestedSymbolNames.TryGetValue(eid, out var symName))
                {
                    map[ft.Name] = symName;
                    break;
                }
            }
        }

        return map;
    }

    private sealed class StampFamilyResolution
    {
        /// <summary>Stamp symbol type name → the loaded FamilySymbol in the project.</summary>
        public Dictionary<string, FamilySymbol> StampSymbolsByTypeName = new();

        /// <summary>Fixture type name → stamp symbol type name. Empty for single-graphic families.</summary>
        public Dictionary<string, string> FixtureTypeToStampType = new();

        /// <summary>First loaded stamp symbol — used for single-graphic families and unmapped types.</summary>
        public FamilySymbol? Fallback;
    }
}
