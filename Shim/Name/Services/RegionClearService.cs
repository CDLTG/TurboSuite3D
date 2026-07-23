#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Name.Regions;

namespace TurboSuite.Name.Services;

/// <summary>
/// Collects the room regions — and the TextNotes TurboName placed for them — that a "Clear &amp; regenerate"
/// is about to delete.
///
/// <b>Pure reads.</b> Nothing here opens a transaction; the caller (<c>TurboNameApiHandler.RunAutoGenerate</c>)
/// deletes the returned ids inside the SAME transaction that creates the replacement regions, so the whole
/// clear+regenerate is one Ctrl+Z. Same contract as <see cref="RegionCreationService"/> /
/// <see cref="RegionNamingService"/>.
///
/// <b>Why the note rules are asymmetric.</b> The room-name type (<c>AL_Annotation_4.5"</c>) is effectively
/// TurboName's alone in these views, so type + containment is enough. The description type
/// (<c>AL_Annotation_3"</c>) is a general-purpose annotation type used for all sorts of text, so its type id
/// proves nothing — those notes must additionally look like something
/// <see cref="CeilingHeightFormatter.Clean"/> could have emitted
/// (<see cref="CeilingHeightFormatter.LooksLikeDescriptionNote"/>).
/// </summary>
public static class RegionClearService
{
    private static readonly HashSet<string> ClearableTypeNames = new()
    {
        "Room Region",
        "Room Region (Flagged)",
        "Room Region (Empty)",
    };

    /// <summary>
    /// Every clearable FilledRegion in the view, by id — optionally narrowed to the crop box.
    /// </summary>
    /// <param name="crop">
    /// When active, drops regions belonging to another floor of a stacked DWG. <b>Required for correctness,
    /// not a nicety:</b> a view-scoped <c>FilteredElementCollector</c> ignores the crop entirely, so without
    /// this the clear set spans every floor in the view while the regenerate that follows only ever covers
    /// the cropped one — accepting "Clear all" from level 2 deleted level 1 and never rebuilt it. Pass
    /// <c>null</c> for the un-narrowed set (see the selection path in <c>TurboNameApiHandler.TryPlanClear</c>).
    /// </param>
    /// <remarks>
    /// Deliberately NOT <see cref="RegionCollectorService.CollectRegions"/>: that one drops any region whose
    /// <c>GetBoundaries()</c> is null/empty, which is right for naming but would silently spare a degenerate
    /// region from the clear — and a spared region goes on to block a seed in the auto-generate skip test,
    /// leaving a permanent hole no regenerate can fill. Type match only, so nothing escapes.
    /// </remarks>
    public static List<ElementId> CollectClearableRegions(Document doc, View view, CropScope crop = null)
    {
        return new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .Where(r => !r.IsMasking)
            .Where(r =>
            {
                var typeId = r.GetTypeId();
                if (typeId == ElementId.InvalidElementId) return false;
                string typeName = doc.GetElement(typeId)?.Name;
                return typeName != null && ClearableTypeNames.Contains(typeName);
            })
            .Where(r => crop == null || crop.ContainsElement(r, view))
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// The TextNotes to delete alongside <paramref name="regionsBeingCleared"/>.
    /// </summary>
    /// <param name="allRegionsInView">
    /// Every clearable region in the view WITH boundaries (from <see cref="RegionCollectorService"/>) — needed
    /// even in selection mode, because the orphan test asks "inside no region at all", which is about the whole
    /// view, not just the cleared subset.
    /// </param>
    /// <param name="regionsBeingCleared">The subset actually being deleted. Notes inside these go with them.</param>
    /// <param name="includeOrphans">
    /// Clear-all only. Also collects TurboName-shaped notes that sit inside NO region in the view — the debris
    /// left behind when a user hand-deleted a region earlier and its notes stayed. Never set in selection mode:
    /// sweeping notes far outside the selected wing would be a surprise.
    /// </param>
    /// <param name="crop">
    /// Bounds the ORPHAN sweep only. Every note on another floor of a stacked DWG is an orphan as far as this
    /// view is concerned — <paramref name="allRegionsInView"/> is itself crop-scoped — so an unbounded sweep
    /// would delete the neighbouring floor's labels wholesale. Notes inside a region actually being cleared are
    /// deliberately NOT crop-tested: if the region goes, its notes go, even where a region straddling the crop
    /// edge put one just outside the box. Leaving those behind is how you manufacture the very orphans this
    /// sweep exists to collect.
    /// </param>
    public static List<ElementId> CollectNotes(
        Document doc, View view,
        List<RegionData> allRegionsInView,
        List<RegionData> regionsBeingCleared,
        ElementId nameTypeId, ElementId descTypeId,
        bool includeOrphans,
        CropScope crop = null)
    {
        var notes = new List<ElementId>();

        foreach (var note in new FilteredElementCollector(doc, view.Id)
                     .OfClass(typeof(TextNote))
                     .Cast<TextNote>())
        {
            var typeId = note.GetTypeId();
            bool isName = nameTypeId != ElementId.InvalidElementId && typeId == nameTypeId;
            bool isDesc = descTypeId != ElementId.InvalidElementId && typeId == descTypeId
                          && CeilingHeightFormatter.LooksLikeDescriptionNote(note.Text);
            if (!isName && !isDesc) continue;

            if (regionsBeingCleared.Any(r => RegionNamingService.IsPointInZone(r.BoundaryLoops, note.Coord)))
            {
                notes.Add(note.Id);
                continue;
            }

            if (includeOrphans &&
                (crop == null || crop.Contains(note.Coord)) &&
                !allRegionsInView.Any(r => RegionNamingService.IsPointInZone(r.BoundaryLoops, note.Coord)))
            {
                notes.Add(note.Id);
            }
        }

        return notes;
    }
}
