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

    // One-shot watershed partition of the whole floor (no pick loop): partition + vectorize, then create
    // every territory as a FilledRegion in a single transaction (one Ctrl+Z; individual failures skipped).
    private void RunAutoGenerate(TurboNameRequest request)
    {
        var settings = _settingsProvider();
        var regionTypeId = ResolveRegionTypeId(settings, out string error);
        if (regionTypeId == ElementId.InvalidElementId) { ReportError(request, error); return; }

        string report;
        int created = 0, failed = 0;
        try
        {
            var result = RegionWatershedService.Run(_doc, _view, settings);
            report = result.Report;

            if (result.Regions.Count > 0)
            {
                var failures = new List<string>();
                using var tx = new Transaction(_doc, "TurboName - Auto-generate Regions");
                tx.Start();
                foreach (var region in result.Regions)
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
                NudgeImportGraphics();
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
