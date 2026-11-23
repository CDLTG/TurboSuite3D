using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Shared.Storage;

/// <summary>
/// The storage generation this build writes. A project stamped with a <b>higher</b> generation was saved by
/// a newer build whose stores this build can't see (a replacement schema GUID) or can't safely round-trip (an
/// incompatible JSON payload), so this build refuses its ExtensibleStorage saves there instead of splitting
/// the project's data across two stores.
///
/// <para><b>Bump only</b> when a release ships a replacement schema GUID or a non-backward-compatible
/// <c>PayloadVersion</c> — never for a sibling schema, a removed field, or an additive payload field. It
/// must be an explicit constant: a dev build's assembly version equals the last release until <c>/release</c>
/// bumps it, so the version number can't tell a dev build from the release it follows.</para>
/// </summary>
public static class StorageGeneration
{
    /// <summary>This build's generation. 1 = the seed release that first carries the guard.</summary>
    public const int Current = 1;
}

/// <summary>
/// The pure rules of the storage-generation guard. The shim reads every stamp in the document and asks these;
/// see <c>Specs/_Plans/fizzy-enchanting-meteor.md</c> §2c for the design.
/// </summary>
public static class StorageGenerationGate
{
    /// <summary>A document with no stamp: written only by pre-guard builds (or never).</summary>
    public const int Unstamped = 0;

    /// <summary>
    /// A document's generation = the <b>max</b> over every stamp it carries. The stamp store is append-only
    /// (raising it creates a new element, so it needs no worksharing borrow), and two users can each stamp
    /// before either syncs — so there may be several, and the highest one wins.
    /// </summary>
    public static int DocGeneration(IEnumerable<int>? stamps)
    {
        int max = Unstamped;
        if (stamps == null) return max;
        foreach (var s in stamps)
            if (s > max) max = s;
        return max;
    }

    /// <summary>True when a build of <paramref name="buildGen"/> may save ExtensibleStorage into a document of
    /// <paramref name="docGen"/> — i.e. no newer build has written it.</summary>
    public static bool CanWrite(int docGen, int buildGen) => docGen <= buildGen;

    /// <summary>
    /// True when a guarded save should also write a stamp. Only when the build is newer than both the document
    /// and generation 1: stamping 1 protects nothing (no guarded build is below 1), so the seed release adds
    /// no element to any project. Never in a template — nobody works live in one, and a stamp there would copy
    /// into every project created from it.
    /// </summary>
    public static bool ShouldStamp(int docGen, int buildGen, bool isTemplate) =>
        !isTemplate && buildGen > Math.Max(docGen, 1);

    /// <summary>True for a Revit project template path (<c>.rte</c>). A project newly created from a template
    /// has an empty path until saved, so it is not a template.</summary>
    public static bool IsTemplatePath(string? path) =>
        !string.IsNullOrEmpty(path) && path!.EndsWith(".rte", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What happened to an ExtensibleStorage save.</summary>
public enum StorageSaveOutcome
{
    /// <summary>Committed.</summary>
    Saved,

    /// <summary>Revit rolled the transaction back (a worksharing denial) — report
    /// <c>WorksharingMessages.RolledBack</c>.</summary>
    RolledBack,

    /// <summary>Refused before writing: the document belongs to a newer storage generation, or its stored
    /// payload couldn't be read. The guard owns the user message; callers stay silent.</summary>
    Blocked
}

/// <summary>Where a store's data comes from when a replacement schema GUID shipped (carry-forward reads).</summary>
public enum StoreSource { None, Current, Legacy }

/// <summary>The store to read: the current schema's, else the newest legacy schema's present, else none.</summary>
public readonly struct StoreChoice
{
    public StoreChoice(StoreSource source, int legacyIndex)
    {
        Source = source;
        LegacyIndex = legacyIndex;
    }

    public StoreSource Source { get; }

    /// <summary>Index into the legacy list (newest-first) when <see cref="Source"/> is
    /// <see cref="StoreSource.Legacy"/>; otherwise -1.</summary>
    public int LegacyIndex { get; }
}

/// <summary>
/// Carry-forward choice for a store whose schema GUID was replaced: read the current store if the project has
/// one; otherwise the newest legacy store present (mapped by the service's legacy reader); otherwise nothing.
/// No write happens at load — the first normal save creates the current store and leaves the legacy one in place.
/// </summary>
public static class StoreSourceChooser
{
    public static StoreChoice Choose(bool currentPresent, IReadOnlyList<bool>? legacyPresentNewestFirst)
    {
        if (currentPresent) return new StoreChoice(StoreSource.Current, -1);
        if (legacyPresentNewestFirst != null)
            for (int i = 0; i < legacyPresentNewestFirst.Count; i++)
                if (legacyPresentNewestFirst[i]) return new StoreChoice(StoreSource.Legacy, i);
        return new StoreChoice(StoreSource.None, -1);
    }
}

/// <summary>
/// JSON stores (DMX, DALI, CadRoomSource): never overwrite a payload this build couldn't parse. Today an
/// unparseable payload loads as an empty state, and the next (auto-)save writes that empty state over it.
/// Deliberately <b>not</b> version-based — an additive <c>PayloadVersion</c> bump must not block older builds;
/// an incompatible one already bumps <see cref="StorageGeneration"/>.
/// </summary>
public static class PayloadGate
{
    /// <summary>True when the store may be saved: nothing was stored, or what was stored parsed.</summary>
    public static bool CanWrite(bool storedPresent, bool parsed) => !storedPresent || parsed;
}
