#nullable disable
using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Name.Services;

/// <summary>
/// IExternalEventHandler that runs pick loops for region generation.
/// Supports rectangle mode (two clicks) and polygon mode (multi-click, Escape to close).
/// </summary>
public class RegionPickHandler : IExternalEventHandler
{
    private readonly Document _doc;
    private readonly UIDocument _uidoc;
    private readonly View _view;
    private readonly ElementId _regionTypeId;

    public RegionGenerationRequest CurrentRequest { get; set; }

    public RegionPickHandler(Document doc, UIDocument uidoc, View view, ElementId regionTypeId)
    {
        _doc = doc;
        _uidoc = uidoc;
        _view = view;
        _regionTypeId = regionTypeId;
    }

    public void Execute(UIApplication app)
    {
        var request = CurrentRequest;
        if (request == null) return;

        try
        {
            if (request is RectanglePickRequest)
                RunRectangleLoop(request);
            else if (request is PolygonPickRequest)
                RunPolygonLoop(request);
        }
        catch (Exception)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                request.OnComplete?.Invoke(new PickLoopUpdate(0, 0, true));
            }));
        }
    }

    private void RunRectangleLoop(RegionGenerationRequest request)
    {
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

            CreateRegionAndNotify(request, boundary, ref created, ref failed);
        }

        NotifyLoopEnded(request, created, failed);
    }

    private void RunPolygonLoop(RegionGenerationRequest request)
    {
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

            CreateRegionAndNotify(request, points, ref created, ref failed);
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

    private void CreateRegionAndNotify(RegionGenerationRequest request, List<XYZ> boundary,
        ref int created, ref int failed)
    {
        ElementId regionId;
        using (var tx = new Transaction(_doc, "TurboName - Generate Region"))
        {
            tx.Start();
            regionId = RegionCreationService.CreateRegion(_doc, _view, boundary, _regionTypeId);
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
        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
        {
            request.OnComplete?.Invoke(new PickLoopUpdate(c, f, false));
        }));
    }

    private void NotifyLoopEnded(RegionGenerationRequest request, int created, int failed)
    {
        int c = created, f = failed;
        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
        {
            request.OnComplete?.Invoke(new PickLoopUpdate(c, f, true));
        }));
    }

    public string GetName() => "TurboName Region Pick Handler";
}
