#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Copies selected levels from the linked architectural document into the host, then deletes
/// the host template's original levels. Single responsibility; caller owns the transaction.
/// </summary>
internal static class LevelCopyService
{
    /// <summary>
    /// Suppresses the "duplicate types" prompt during cross-document paste by always keeping
    /// the destination (host template) types.
    /// </summary>
    private sealed class UseDestinationTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            => DuplicateTypeAction.UseDestinationTypes;
    }

    /// <summary>
    /// Within an open transaction: rename the host's pre-existing levels out of the way, then
    /// copy <paramref name="sourceLevelIds"/> from <paramref name="linkDoc"/> using the link
    /// instance's transform. Returns a map from each source level id to its newly-created host
    /// <see cref="Level"/>. Does NOT delete the originals — see <see cref="DeleteOriginalLevels"/>,
    /// which must run after the active view has been moved off any original-level view.
    /// </summary>
    /// <param name="hostDoc">Active host document.</param>
    /// <param name="linkDoc">The linked architectural document.</param>
    /// <param name="linkTransform">The link instance's total transform (places levels correctly).</param>
    /// <param name="sourceLevelIds">Levels to copy, in elevation order.</param>
    /// <param name="originalHostLevelIds">Snapshot of host level ids captured before any copy.</param>
    public static Dictionary<ElementId, Level> CopyLevels(
        Document hostDoc,
        Document linkDoc,
        Transform linkTransform,
        IList<ElementId> sourceLevelIds,
        IList<ElementId> originalHostLevelIds)
    {
        // 1. Rename the originals to guaranteed-unique temp names so an incoming arch level that
        //    shares a name (e.g. "Level 1") keeps its exact name instead of being auto-suffixed.
        foreach (var id in originalHostLevelIds)
        {
            if (hostDoc.GetElement(id) is Level original)
                original.Name = $"zzTS_old_{original.Id.GetHashCode()}_{original.Name}";
        }

        // 2. Source levels sorted ascending by elevation — the order we'll match copies back in.
        var sortedSources = sourceLevelIds
            .Select(id => (Level)linkDoc.GetElement(id))
            .Where(l => l != null)
            .OrderBy(l => l.Elevation)
            .ToList();

        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypesHandler());

        var copiedIds = ElementTransformUtils.CopyElements(
            linkDoc,
            sortedSources.Select(l => l.Id).ToList(),
            hostDoc,
            linkTransform ?? Transform.Identity,
            options);

        // 3. Match copies back to sources. A copy with a monotonic transform preserves elevation
        //    ordering, so sort the new host levels ascending and zip with the sorted sources.
        var newLevels = copiedIds
            .Select(id => hostDoc.GetElement(id))
            .OfType<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        var map = new Dictionary<ElementId, Level>();
        for (int i = 0; i < sortedSources.Count && i < newLevels.Count; i++)
            map[sortedSources[i].Id] = newLevels[i];

        return map;
    }

    /// <summary>
    /// Within an open transaction: delete the host template's original levels. The host already
    /// holds the copied levels, so this won't hit the "can't delete the last level" rule. Tries a
    /// single batch delete (one consolidated cascade warning); if Revit rejects the batch because
    /// one level can't be deleted (e.g. it still hosts the active view), falls back to per-level
    /// deletes and skips the offenders. Returns the number of levels actually deleted.
    /// The cascade warning (dependent placeholder views/annotation) is intentional, not suppressed.
    /// </summary>
    public static int DeleteOriginalLevels(Document hostDoc, IList<ElementId> originalHostLevelIds)
    {
        var toDelete = originalHostLevelIds.Where(id => hostDoc.GetElement(id) != null).ToList();
        if (toDelete.Count == 0)
            return 0;

        try
        {
            hostDoc.Delete(toDelete);
            return toDelete.Count;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // At least one level couldn't be deleted as part of the batch — delete what we can.
            int deleted = 0;
            foreach (var id in toDelete)
            {
                try
                {
                    if (hostDoc.GetElement(id) != null)
                    {
                        hostDoc.Delete(id);
                        deleted++;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // This level can't be deleted (still hosting the active view, pinned, etc.) — skip it.
                }
            }
            return deleted;
        }
    }
}
