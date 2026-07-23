#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Single <see cref="IExternalEventHandler"/> behind the modeless TurboName window — the one valid API
/// context for every Revit write (see CLAUDE.md "Modeless pattern"). Handles interactive region pick loops
/// (rectangle / polygon), one-shot auto-generate, the pick-from-view CAD-layer probe, Assign Room Names, and
/// settings persistence. The ViewModel queues exactly one <see cref="TurboNameRequest"/> per
/// <see cref="ExternalEvent.Raise"/>; it never raises a second while one is in flight.
/// </summary>
public class TurboNameApiHandler : IExternalEventHandler
{
    private readonly Document _doc;
    private readonly UIDocument _uidoc;
    private readonly View _view;
    private readonly Func<CadRoomSourceSettings> _settingsProvider;
    private readonly string _textNoteTypeName;
    private readonly string _descTextNoteTypeName;

    /// <summary>Transient red role-preview overlay, owned here so its snapshots survive across requests and can
    /// be reverted in one place on close.</summary>
    public LayerRolePreviewService RolePreview { get; } = new();

    public TurboNameRequest CurrentRequest { get; set; }

    public TurboNameApiHandler(Document doc, UIDocument uidoc, View view,
        Func<CadRoomSourceSettings> settingsProvider, string textNoteTypeName, string descTextNoteTypeName)
    {
        _doc = doc;
        _uidoc = uidoc;
        _view = view;
        _settingsProvider = settingsProvider;
        _textNoteTypeName = textNoteTypeName;
        _descTextNoteTypeName = descTextNoteTypeName;
    }

    public void Execute(UIApplication app)
    {
        var request = CurrentRequest;
        if (request == null) return;

        try
        {
            switch (request)
            {
                case RectanglePickRequest:
                    RunRectangleLoop(request);
                    break;
                case PolygonPickRequest:
                    RunPolygonLoop(request);
                    break;
                case AutoGeneratePickRequest:
                    RunAutoGenerate(request);
                    break;
                case AssignNamesRequest:
                    RunAssignNames(request);
                    break;
                case PickLayerRequest pick:
                    pick.Pick?.Invoke();
                    Finish(request);
                    break;
                case SetLayerVisibilityRequest vis:
                    RunSetVisibility(vis);
                    Finish(request);
                    break;
                case HideLayerPickRequest hidePick:
                    RunHideLayerPickLoop(hidePick);
                    break;
                case PaintRolePreviewsRequest paintAll:
                    RunPaintRolePreviews(paintAll);
                    Finish(request);
                    break;
                case ApplyLineGraphicsRequest lineGfx:
                    RunApplyLineGraphics(lineGfx);
                    Finish(request);
                    break;
                case CloseCleanupRequest cleanup:
                    RunCloseCleanup(cleanup);
                    Finish(request);
                    break;
            }
        }
        catch (Exception)
        {
            Dispatch(() => request.OnComplete?.Invoke(new PickLoopUpdate(0, 0, true)));
            Finish(request);
        }
    }

    // ── Region type resolution (from the live settings each time) ──
    private ElementId ResolveRegionTypeId(CadRoomSourceSettings settings, out string error)
    {
        error = null;
        string regionTypeName = string.IsNullOrEmpty(settings.RegionTypeName) ? "Room Region" : settings.RegionTypeName;
        var regionType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .FirstOrDefault(t => t.Name == regionTypeName);
        if (regionType == null)
        {
            error = $"FilledRegionType \"{regionTypeName}\" not found in project.\n\n" +
                    "Create this type or update the Region Type Name in Settings.";
            return ElementId.InvalidElementId;
        }
        return regionType.Id;
    }

    private void RunRectangleLoop(TurboNameRequest request)
    {
        var settings = _settingsProvider();
        var regionTypeId = ResolveRegionTypeId(settings, out string error);
        if (regionTypeId == ElementId.InvalidElementId) { ReportError(request, error); return; }

        int created = 0;
        int failed = 0;

        while (true)
        {
            XYZ corner1;
            try
            {
                corner1 = _uidoc.Selection.PickPoint("Click first corner (Escape to finish)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }

            XYZ corner2;
            try
            {
                corner2 = _uidoc.Selection.PickPoint("Click opposite corner (Escape to cancel)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }

            double z = corner1.Z;
            var boundary = new List<XYZ>
            {
                new XYZ(corner1.X, corner1.Y, z),
                new XYZ(corner2.X, corner1.Y, z),
                new XYZ(corner2.X, corner2.Y, z),
                new XYZ(corner1.X, corner2.Y, z)
            };

            CreateRegionAndNotify(request, regionTypeId, boundary, ref created, ref failed);
        }

        NotifyLoopEnded(request, created, failed);
    }

    private void RunPolygonLoop(TurboNameRequest request)
    {
        var settings = _settingsProvider();
        var regionTypeId = ResolveRegionTypeId(settings, out string error);
        if (regionTypeId == ElementId.InvalidElementId) { ReportError(request, error); return; }

        int created = 0;
        int failed = 0;

        while (true)
        {
            var points = new List<XYZ>();
            var guideLineIds = new List<ElementId>();

            while (true)
            {
                XYZ pt;
                try
                {
                    string prompt = points.Count == 0
                        ? "Click first corner (Escape to finish)"
                        : $"Click next corner — {points.Count} so far (Escape to close shape)";
                    pt = _uidoc.Selection.PickPoint(prompt);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }

                // Draw a guide line from the previous point to this one
                if (points.Count > 0)
                {
                    var lineId = DrawGuideLine(points[points.Count - 1], pt);
                    if (lineId != ElementId.InvalidElementId)
                        guideLineIds.Add(lineId);
                }

                points.Add(pt);
            }

            // Delete guide lines before creating the region
            DeleteGuideLines(guideLineIds);

            // Escape with fewer than 3 points — exit entirely
            if (points.Count < 3)
                break;

            CreateRegionAndNotify(request, regionTypeId, points, ref created, ref failed);
        }

        NotifyLoopEnded(request, created, failed);
    }

    private ElementId DrawGuideLine(XYZ from, XYZ to)
    {
        try
        {
            using (var tx = new Transaction(_doc, "TurboName - Guide Line"))
            {
                tx.Start();
                var line = Line.CreateBound(from, to);
                var detailLine = _doc.Create.NewDetailCurve(_view, line);

                // Apply a distinct line style if available
                var lineStyle = FindLineStyle("Wiring (Green)");
                if (lineStyle != null)
                    detailLine.LineStyle = lineStyle;

                tx.Commit();
                return detailLine.Id;
            }
        }
        catch
        {
            return ElementId.InvalidElementId;
        }
    }

    private Element FindLineStyle(string name)
    {
        var linesCategory = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        foreach (Category subCat in linesCategory.SubCategories)
        {
            if (subCat.Name == name)
                return subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
        return null;
    }

    private void DeleteGuideLines(List<ElementId> lineIds)
    {
        if (lineIds.Count == 0) return;
        try
        {
            using (var tx = new Transaction(_doc, "TurboName - Remove Guide Lines"))
            {
                tx.Start();
                _doc.Delete(lineIds);
                tx.Commit();
            }
        }
        catch { }
    }

    private void CreateRegionAndNotify(TurboNameRequest request, ElementId regionTypeId, List<XYZ> boundary,
        ref int created, ref int failed)
    {
        ElementId regionId;
        using (var tx = new Transaction(_doc, "TurboName - Generate Region"))
        {
            tx.Start();
            regionId = RegionCreationService.CreateRegion(_doc, _view, boundary, regionTypeId);
            if (regionId != ElementId.InvalidElementId)
                tx.Commit();
            else
                tx.RollBack();
        }

        if (regionId != ElementId.InvalidElementId)
            created++;
        else
            failed++;

        int c = created, f = failed;
        Dispatch(() => request.OnComplete?.Invoke(new PickLoopUpdate(c, f, false)));
    }

    /// <summary>What a Clear &amp; regenerate is about to delete — resolved before the prompt so the counts
    /// shown to the user are the real ones, and reused verbatim as the delete set if they accept.</summary>
    private sealed class ClearPlan
    {
        public List<ElementId> RegionIds = new();
        public List<ElementId> NoteIds = new();
        public int Count => RegionIds.Count + NoteIds.Count;
        public string Describe() => $"Deletes {RegionIds.Count} region(s) + {NoteIds.Count} text note(s).";
    }

    // One-shot watershed partition of the whole floor (no pick loop): partition + vectorize, then create
    // every territory as a FilledRegion in a single transaction (one Ctrl+Z; individual failures skipped).
    //
    // When regions already exist we stop and ask first, because the partition is whole-floor and
    // unconditional: generating on top of a populated view stacks a second complete set, and two coincident
    // FilledRegions are near-invisible. The chosen clear runs in the SAME transaction as the creation, so a
    // bad regenerate is one Ctrl+Z away and that undo restores the deleted regions AND their notes.
    private void RunAutoGenerate(TurboNameRequest request)
    {
        var settings = _settingsProvider();
        var regionTypeId = ResolveRegionTypeId(settings, out string error);
        if (regionTypeId == ElementId.InvalidElementId) { ReportError(request, error); return; }

        // ── Decide what (if anything) to clear, before touching the model ──
        ClearPlan clear;
        try
        {
            if (!TryPlanClear(out clear)) { Finish(request); return; }   // user cancelled
        }
        catch (Exception ex)
        {
            ReportError(request, $"Could not inspect the existing regions:\n{ex.Message}");
            return;
        }

        string report;
        int created = 0, failed = 0;
        try
        {
            var result = RegionWatershedService.Run(_doc, _view, settings);
            report = result.Report;

            // Territories already covered by a region that SURVIVES the clear are skipped. Load-bearing for
            // "Clear selected": the watershed re-partitions the whole floor regardless of what was cleared, so
            // this is the only thing keeping the survivors from being duplicated.
            //
            // Collected UNSCOPED, unlike the clear planner, and it costs nothing: every generated seed is
            // crop-clipped, so an out-of-crop survivor can never contain one and never suppresses anything.
            // After "Clear all" the in-crop survivors are gone, which leaves this a no-op on this floor.
            //
            // Point test, so it prevents a duplicate per seed — not geometric overlap. Where a newly generated
            // territory abuts a surviving HAND-DRAWN region, expect a hairline gap or overlap on the shared
            // edge: the hand-drawn edge is wherever the user clicked, the new one is wall-aligned by
            // RegionVectorizer. Against a surviving auto-generated neighbour on an unchanged wall they match.
            var cleared = new HashSet<ElementId>(clear.RegionIds);
            var survivors = RegionCollectorService.CollectRegions(_doc, _view)
                .Where(r => !cleared.Contains(r.RegionId))
                .ToList();

            var toCreate = result.Regions
                .Where(r => !survivors.Any(s => RegionNamingService.IsPointInZone(s.BoundaryLoops, r.Seed)))
                .ToList();
            int covered = result.Regions.Count - toCreate.Count;

            if (clear.Count > 0 || toCreate.Count > 0)
            {
                var failures = new List<string>();

                // TransactionGroup + Assimilate so the whole thing is ONE undo entry. Without it the stack
                // ends up [Auto-generate Regions][Refresh CAD][Refresh CAD] — NudgeImportGraphics commits two
                // more transactions of its own, and it has to, because the CAD regen it forces only happens
                // post-commit. Ctrl+Z would then just toggle a pin. That was survivable when this only ever
                // ADDED regions; it is not, now that one keystroke is the advertised way back from a clear.
                //
                // Assimilate is used here purely to collapse the undo stack. It buys nothing against the
                // return-value discard documented in CLAUDE.md — irrelevant anyway, since a modeless handler
                // never returns a Result.
                using var group = new TransactionGroup(_doc, "TurboName - Auto-generate Regions");
                group.Start();

                using (var tx = new Transaction(_doc, "TurboName - Auto-generate Regions"))
                {
                    tx.Start();

                    // Delete first, then create — same transaction, so the clear and the regenerate undo together.
                    if (clear.NoteIds.Count > 0) _doc.Delete(clear.NoteIds);
                    if (clear.RegionIds.Count > 0) _doc.Delete(clear.RegionIds);

                    foreach (var region in toCreate)
                    {
                        var id = RegionCreationService.CreateRegion(_doc, _view, region.Boundary,
                            regionTypeId, out string reason);
                        if (id != ElementId.InvalidElementId) created++;
                        else
                        {
                            failed++;
                            string name = string.IsNullOrWhiteSpace(region.RoomName) ? "(unnamed)" : region.RoomName;
                            failures.Add($"    {name}: {reason} [{region.Boundary.Count} pts]");
                        }
                    }
                    tx.Commit();
                }

                NudgeImportGraphics();
                group.Assimilate();

                if (clear.Count > 0)
                    report += $"\n\nCleared {clear.RegionIds.Count} region(s) + {clear.NoteIds.Count} text note(s).";
                if (covered > 0)
                    report += $"\nSkipped {covered} territor{(covered == 1 ? "y" : "ies")} already covered by an existing region.";
                report += $"\n\nCreated {created} region(s)" + (failed > 0 ? $", {failed} failed" : "") + ".";
                if (failures.Count > 0)
                    report += "\nFailures:\n" + string.Join("\n", failures);
            }
        }
        catch (Exception ex)
        {
            report = $"Auto-generate failed:\n{ex}";
        }

        int c = created, f = failed;
        Dispatch(() =>
        {
            request.OnComplete?.Invoke(new PickLoopUpdate(c, f, true, report));
            TaskDialog.Show("TurboName — Auto-generate", report);
        });
        Finish(request);
    }

    /// <summary>
    /// Builds the clear plan, prompting when the view already holds regions. Returns false only if the user
    /// cancelled; an empty plan (nothing to clear, or nothing there to begin with) returns true.
    /// </summary>
    private bool TryPlanClear(out ClearPlan plan)
    {
        plan = new ClearPlan();

        // Everything here is scoped to the crop box, because the crop is what says which floor of a stacked
        // DWG the user is on and the regenerate that follows is crop-clipped too. A view-scoped collector is
        // NOT crop-aware, so without this the prompt counted every floor's regions and "Clear all" deleted
        // them — while the watershed rebuilt only the cropped floor. Inactive crop ⇒ no-op, as before.
        var crop = CropScope.For(_view);

        var clearable = RegionClearService.CollectClearableRegions(_doc, _view, crop);
        if (clearable.Count == 0) return true;   // nothing on this floor — generate exactly as before, no prompt

        // Boundaries for the containment tests. CollectRegions drops boundary-less regions, so this is a
        // subset of `clearable` — which is why the DELETE set comes from `clearable` and only the note
        // containment comes from here. Crop-scoped to match: it feeds the orphan test, and a note is only an
        // orphan relative to the regions of its own floor.
        var withBoundaries = RegionCollectorService.CollectRegions(_doc, _view)
            .Where(r => crop.ContainsElement(_doc.GetElement(r.RegionId), _view))
            .ToList();
        var nameTypeId = ResolveTextNoteTypeId(_textNoteTypeName);
        var descTypeId = ResolveTextNoteTypeId(_descTextNoteTypeName);

        // Pre-selection, not a pick loop: the user selects regions in the view, then presses Auto-generate.
        // A pick loop would hide the button row and can't be cancelled from the window, so it fights the
        // modeless architecture. Spike-confirmed (Revit 2025) that the selection SURVIVES the focus change
        // into the modeless window: 3 regions selected, clicked into the window, read from inside this
        // handler → GetElementIds().Count = 3, all resolving to FilledRegion/"Room Region" owned by the
        // active view. Without that the second command link could never appear.
        //
        // Intersected with the crop-scoped `clearable`, so a selection made BEFORE the crop moved cannot leak
        // in. Those regions are invisible under the current crop and would not be regenerated — clearing them
        // is the same silent floor-deletion the crop scoping exists to prevent, just arrived at via a stale
        // selection instead of via "Clear all".
        var selectedIds = new HashSet<ElementId>(_uidoc.Selection.GetElementIds());
        var selectedRegions = clearable.Where(selectedIds.Contains).ToList();

        // Both plans costed up front so neither command link is a blind press.
        var all = new ClearPlan
        {
            RegionIds = clearable,
            NoteIds = RegionClearService.CollectNotes(_doc, _view, withBoundaries, withBoundaries,
                nameTypeId, descTypeId, includeOrphans: true, crop: crop),
        };

        ClearPlan selected = null;
        if (selectedRegions.Count > 0)
        {
            var selectedWithBoundaries = withBoundaries
                .Where(r => selectedIds.Contains(r.RegionId)).ToList();
            selected = new ClearPlan
            {
                RegionIds = selectedRegions,
                NoteIds = RegionClearService.CollectNotes(_doc, _view, withBoundaries, selectedWithBoundaries,
                    nameTypeId, descTypeId, includeOrphans: false, crop: crop),
            };
        }

        var dlg = new TaskDialog("TurboName — Auto-generate")
        {
            MainInstruction = $"{clearable.Count} room region(s) already exist "
                            + (crop.IsActive ? "inside this view's crop." : "in this view."),
            MainContent = "Auto-generate re-partitions the whole floor, so generating on top of them "
                        + "would create a duplicate set. Clearing and regenerating is one undo step."
                        + (crop.IsActive
                            ? "\n\nOnly what the crop box covers is counted, cleared, or regenerated — "
                              + "regions on other floors of a stacked DWG are left alone."
                            : ""),
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        // "and", not "&": TaskDialog command-link text goes through Win32's mnemonic parser, which eats a bare
        // '&' as an accelerator prefix ("Clear all & regenerate" rendered as "Clear all  regenerate"). Escaping
        // it as "&&" works but is obscure enough to get "fixed" back into a single & later.
        dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Clear all and regenerate", all.Describe());
        if (selected != null)
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                $"Clear selected ({selected.RegionIds.Count}) and regenerate", selected.Describe());

        switch (dlg.Show())
        {
            case TaskDialogResult.CommandLink1: plan = all; return true;
            case TaskDialogResult.CommandLink2: plan = selected; return true;
            default: return false;
        }
    }

    private ElementId ResolveTextNoteTypeId(string typeName) =>
        new FilteredElementCollector(_doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault(t => t.Name == typeName)?.Id ?? ElementId.InvalidElementId;

    // Assign Room Names — moved out of NameCommand.Execute when TurboName went modeless. Collects Room Region
    // filled regions, extracts CAD room data, assigns names + places TextNotes in one transaction, flags
    // ambiguous/unmatched regions, then reports a summary. Validation failures surface as a TaskDialog.
    private void RunAssignNames(TurboNameRequest request)
    {
        var settings = _settingsProvider();

        if (string.IsNullOrEmpty(settings.BlockName) && string.IsNullOrEmpty(settings.RoomNameLayer))
        {
            ReportError(request,
                "CAD Room Source is not configured.\n\n" +
                "Configure the CAD Room Source (Block or Text mode) in the TurboName window before running.");
            return;
        }

        var textNoteType = new FilteredElementCollector(_doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault(t => t.Name == _textNoteTypeName);

        if (textNoteType == null)
        {
            ReportError(request,
                $"TextNote type \"{_textNoteTypeName}\" not found in this document.\n\n" +
                "Load the annotation type into the project before running TurboName.");
            return;
        }

        var regions = RegionCollectorService.CollectRegions(_doc, _view);
        if (regions.Count == 0)
        {
            ReportError(request,
                "No \"Room Region\" filled regions found in the active view.\n\n" +
                "Draw filled regions using the \"Room Region\" type, then run TurboName.");
            return;
        }

        var cadRoomData = CadRoomExtractorService.ExtractRoomData(_doc, _view, settings);
        if (cadRoomData.Count == 0)
        {
            ReportError(request,
                "No room data found in linked CAD files.\n\n" +
                "Verify the CAD Room Source settings match the linked DWG content.");
            return;
        }

        var descTextNoteType = new FilteredElementCollector(_doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault(t => t.Name == _descTextNoteTypeName);
        ElementId descTypeId = descTextNoteType?.Id ?? ElementId.InvalidElementId;

        var roomRegionType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .FirstOrDefault(rt => rt.Name == "Room Region");
        ElementId roomRegionTypeId = roomRegionType?.Id;

        Models.NamingResult result;
        using (var t = new Transaction(_doc, "TurboName - Assign Room Names"))
        {
            t.Start();
            result = RegionNamingService.AssignRoomNames(
                _doc, _view, regions, cadRoomData, textNoteType.Id, descTypeId, roomRegionTypeId);

            if (result.AmbiguousDetails.Count > 0)
            {
                var flaggedType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(rt => rt.Name == "Room Region (Flagged)");

                if (flaggedType != null)
                {
                    foreach (var ar in result.AmbiguousDetails)
                        _doc.GetElement(ar.RegionId)?.ChangeTypeId(flaggedType.Id);
                }
            }

            if (result.UnmatchedRegionIds.Count > 0)
            {
                var emptyType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(rt => rt.Name == "Room Region (Empty)");

                if (emptyType != null)
                {
                    foreach (var id in result.UnmatchedRegionIds)
                        _doc.GetElement(id)?.ChangeTypeId(emptyType.Id);
                }
            }

            t.Commit();
        }

        var summary = $"TurboName Complete\n\n" +
            $"Processed: {result.Processed}\n" +
            $"Skipped (existing Comments): {result.Skipped}\n" +
            $"Ambiguous (multiple names): {result.Ambiguous}\n" +
            $"Unmatched (no CAD data): {result.Unmatched}";

        Dispatch(() => TaskDialog.Show("TurboName", summary));
        Finish(request);
    }

    private void RunSetVisibility(SetLayerVisibilityRequest vis)
    {
        if (vis.SubIds == null || vis.SubIds.Count == 0) return;
        using (var tx = new Transaction(_doc, "TurboName - Toggle CAD Layer"))
        {
            tx.Start();
            foreach (var subId in vis.SubIds)
                LinkedCadLayerService.ApplyHidden(_view, subId, vis.Hidden);
            tx.Commit();
        }
        _uidoc.RefreshActiveView();
    }

    // "Hide by picking" loop (native Query ▸ "Hide in view"): click CAD geometry → resolve the layer behind it →
    // hide that layer in the view, and keep going until Escape. Each hide commits + refreshes on its own so the
    // geometry disappears under the cursor and the next pick can't land on it again. Unresolvable picks (the
    // GraphicsStyle didn't map to a listed layer row) report and keep the loop alive rather than ending it.
    private void RunHideLayerPickLoop(HideLayerPickRequest request)
    {
        var hideable = request.HideableSubIds ?? new HashSet<ElementId>();
        int hidden = 0;

        while (true)
        {
            Reference reference;
            try
            {
                reference = _uidoc.Selection.PickObject(
                    Autodesk.Revit.UI.Selection.ObjectType.PointOnElement,
                    new Shared.Filters.ImportInstanceSelectionFilter(_doc),
                    "Click CAD geometry to hide its layer (Escape to finish)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }

            ElementId subId;
            string layerName;
            try { subId = ResolveLayerSubcategory(reference, hideable, out layerName); }
            catch (Exception) { subId = null; layerName = null; }

            if (subId == null)
            {
                Dispatch(() => request.OnComplete?.Invoke(new LayerHiddenUpdate(
                    null, "Couldn't resolve a CAD layer there — try clicking the geometry itself.")));
                continue;
            }

            using (var tx = new Transaction(_doc, "TurboName - Hide CAD Layer"))
            {
                tx.Start();
                LinkedCadLayerService.ApplyHidden(_view, subId, true);
                tx.Commit();
            }
            _uidoc.RefreshActiveView();

            hidden++;
            var doneId = subId;
            string status = $"Hid layer \"{layerName}\" — {hidden} hidden this session.";
            Dispatch(() => request.OnComplete?.Invoke(new LayerHiddenUpdate(doneId, status)));
        }

        int total = hidden;
        Dispatch(() => request.OnComplete?.Invoke(new LayerHiddenUpdate(
            null, total == 0 ? "Hide by picking: nothing hidden." : $"Hide by picking: {total} layer(s) hidden.")));
        Finish(request);
    }

    // The picked point → the layer subcategory behind it. Same resolution the CAD Room Source pick uses
    // (geometry → GraphicsStyle → GraphicsStyleCategory), but returning the id rather than the name, and gated
    // on the caller's roster: an id we don't recognize as a listed layer is refused, because the one thing that
    // must never happen is hiding the import's PARENT category (the whole DWG). Spike-confirmed that ordinary
    // linework, arcs, text, hatch faces, and block-internal layer-0 geometry all resolve to a real layer.
    private ElementId ResolveLayerSubcategory(Reference reference, HashSet<ElementId> hideable, out string layerName)
    {
        layerName = null;
        if (_doc.GetElement(reference.ElementId) is not ImportInstance import) return null;

        var geomObj = import.GetGeometryObjectFromReference(reference);
        if (geomObj == null) return null;
        if (_doc.GetElement(geomObj.GraphicsStyleId) is not GraphicsStyle style) return null;

        var cat = style.GraphicsStyleCategory;
        if (cat == null || !hideable.Contains(cat.Id)) return null;

        layerName = cat.Name;
        return cat.Id;
    }

    // Global red Preview toggle. ON: un-hide + paint each flagged layer red in one transaction (snapshotting its
    // prior override, which carries any persistent line settings). OFF (Revert): restore every snapshot verbatim,
    // composing the base line settings back. One raise = one transaction = one refresh.
    private void RunPaintRolePreviews(PaintRolePreviewsRequest request)
    {
        if (request.Revert)
        {
            if (!RolePreview.HasActive) return;
            using var revertTx = new Transaction(_doc, "TurboName - Hide Watershed Preview");
            revertTx.Start();
            RolePreview.RevertAll(_doc);
            revertTx.Commit();
            _uidoc.RefreshActiveView();
            return;
        }

        if (request.SubIds == null || request.SubIds.Count == 0) return;
        using (var tx = new Transaction(_doc, "TurboName - Show Watershed Preview"))
        {
            tx.Start();
            foreach (var subId in request.SubIds)
            {
                LinkedCadLayerService.ApplyHidden(_view, subId, false); // un-hide so red shows
                RolePreview.Paint(_view, subId);
            }
            tx.Commit();
        }
        _uidoc.RefreshActiveView();
    }

    // Apply a per-layer Lines override (TurboName-12). Persistent — written straight to the view slot like the
    // visibility checkbox, never reverted on close. The OGS was composed on the WPF thread off a clone of the
    // layer's current override, so surface/halftone survive.
    private void RunApplyLineGraphics(ApplyLineGraphicsRequest request)
    {
        if (request.SubIds == null || request.SubIds.Count == 0 || request.Overrides == null) return;
        using (var tx = new Transaction(_doc, "TurboName - Layer Line Graphics"))
        {
            tx.Start();
            foreach (var subId in request.SubIds)
                _view.SetCategoryOverrides(subId, request.Overrides);
            tx.Commit();
        }
        _uidoc.RefreshActiveView();
    }

    private void RunCloseCleanup(CloseCleanupRequest cleanup)
    {
        // Revert the transient red previews first (own transaction), then persist settings if dirty.
        if (cleanup.RevertPreviews && RolePreview.HasActive)
        {
            using var tx = new Transaction(_doc, "TurboName - Revert Layer Previews");
            tx.Start();
            RolePreview.RevertAll(_doc);
            tx.Commit();
            _uidoc.RefreshActiveView();
        }

        if (cleanup.Settings != null)
        {
            Shared.Services.CadRoomSourceStorageService.Save(_doc, cleanup.Settings);
            Shared.Services.CadRoomSourceSettingsCache.Invalidate();
        }
    }

    private void ReportError(TurboNameRequest request, string message)
    {
        Dispatch(() =>
        {
            TaskDialog.Show("TurboName", message);
            request.OnComplete?.Invoke(new PickLoopUpdate(0, 0, true));
        });
        Finish(request);
    }

    private void NotifyLoopEnded(TurboNameRequest request, int created, int failed)
    {
        if (created > 0) NudgeImportGraphics();
        int c = created, f = failed;
        Dispatch(() => request.OnComplete?.Invoke(new PickLoopUpdate(c, f, true)));
        Finish(request);
    }

    // New filled regions draw over the linked CAD until Revit rebuilds the import's graphics, hiding the
    // room-name text underneath (RefreshActiveView repaints but doesn't regenerate the import). This automates
    // the manual "pin/unpin the CAD" workaround: toggling an import's Pinned state and putting it back forces
    // that regen. Two commits (flip, restore) so each regenerates and the pin state is left unchanged. Best-
    // effort — a quirk cosmetic must never break generation.
    private void NudgeImportGraphics()
    {
        try
        {
            var imports = CadLinkResolver.GetLinkedImports(_doc, _view);
            if (imports.Count == 0) return;
            for (int pass = 0; pass < 2; pass++)
            {
                using var tx = new Transaction(_doc, "TurboName - Refresh CAD");
                tx.Start();
                foreach (var imp in imports) imp.Pinned = !imp.Pinned;
                tx.Commit();
            }
        }
        catch { /* cosmetic only */ }
    }

    private static void Finish(TurboNameRequest request)
    {
        Dispatch(() => request.OnFinished?.Invoke());
    }

    private static void Dispatch(Action action)
    {
        Application.Current?.Dispatcher?.BeginInvoke(action);
    }

    public string GetName() => "TurboName API Handler";
}
