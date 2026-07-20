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
                case SaveSettingsRequest save:
                    RunSave(save);
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

    private void RunSave(SaveSettingsRequest save)
    {
        if (save.Settings == null) return;
        Shared.Services.CadRoomSourceStorageService.Save(_doc, save.Settings);
        Shared.Services.CadRoomSourceSettingsCache.Invalidate();
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
        int c = created, f = failed;
        Dispatch(() => request.OnComplete?.Invoke(new PickLoopUpdate(c, f, true)));
        Finish(request);
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
