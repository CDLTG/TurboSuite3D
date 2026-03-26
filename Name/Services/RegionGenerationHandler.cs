#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// IExternalEventHandler that runs the PickPoint loop for region generation.
/// Each Raise enters the loop; Escape exits it and returns control to the modeless dialog.
/// </summary>
public class RegionGenerationHandler : IExternalEventHandler
{
    private readonly Document _doc;
    private readonly UIDocument _uidoc;
    private readonly View _view;
    private readonly List<CadWallSegment> _bridgedSegments;
    private readonly ElementId _regionTypeId;

    private RasterRegionService _rasterService;

    public RegionGenerationRequest CurrentRequest { get; set; }

    public RegionGenerationHandler(Document doc, UIDocument uidoc, View view,
        List<CadWallSegment> bridgedSegments, ElementId regionTypeId)
    {
        _doc = doc;
        _uidoc = uidoc;
        _view = view;
        _bridgedSegments = bridgedSegments;
        _regionTypeId = regionTypeId;
    }

    public void Execute(UIApplication app)
    {
        var request = CurrentRequest;
        if (request == null) return;

        try
        {
            // Always rebuild raster from bridged segments and scan existing regions
            _rasterService = new RasterRegionService(_bridgedSegments);

            // Export debug bitmap to temp folder
            string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TurboSuite_walls.png");
            try { _rasterService.ExportDebugBitmap(debugPath); } catch { debugPath = "export failed"; }

            // Send bitmap diagnostic info
            string initStatus = $"Seg: {_bridgedSegments.Count} ({_bridgedSegments.Count(s => s.IsVirtual)} virtual), {GapBridgingService.LastBridgeInfo}, {_rasterService.DiagnosticInfo}";
            var claimedCount = ClaimExistingRegions();
            initStatus += $", Claimed: {claimedCount}, Bitmap: {debugPath}";
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                request.OnComplete?.Invoke(new PickLoopUpdate(0, 0, false, initStatus));
            }));

            int created = 0;
            int failed = 0;

            while (true)
            {
                XYZ clickPt;
                try
                {
                    clickPt = _uidoc.Selection.PickPoint("Click inside a room (Escape to pause)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break; // User pressed Escape
                }

                var boundary = _rasterService.FillFromPoint(clickPt);
                if (boundary == null)
                {
                    // Send diagnostic status on WPF thread
                    string status = _rasterService.LastStatus;
                    int c2 = created, f2 = failed;
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        request.OnComplete?.Invoke(new PickLoopUpdate(c2, f2, false, status));
                    }));
                    continue;
                }

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
                {
                    created++;
                }
                else
                {
                    failed++;
                    _rasterService.UndoLastFill(); // Un-claim pixels if region creation failed
                }

                // Update dialog counts on WPF thread
                int c = created, f = failed;
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    request.OnComplete?.Invoke(new PickLoopUpdate(c, f, false));
                }));
            }

            // Loop exited (Escape) — notify dialog
            int fc = created, ff = failed;
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                request.OnComplete?.Invoke(new PickLoopUpdate(fc, ff, true));
            }));
        }
        catch (Exception)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                request.OnComplete?.Invoke(new PickLoopUpdate(0, 0, true));
            }));
        }
    }

    public string GetName() => "TurboName Region Generation Handler";

    private int ClaimExistingRegions()
    {
        var existingRegions = new FilteredElementCollector(_doc, _view.Id)
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .ToList();

        int count = 0;
        foreach (var region in existingRegions)
        {
            var typeId = region.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;
            var regionType = _doc.GetElement(typeId);
            if (regionType == null) continue;

            var boundaries = region.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0) continue;

            var loopPoints = new List<List<XYZ>>();
            foreach (var loop in boundaries)
            {
                var points = new List<XYZ>();
                foreach (var curve in loop)
                {
                    var tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++)
                        points.Add(tessellated[i]);
                }
                loopPoints.Add(points);
            }

            _rasterService.ClaimExistingRegion(loopPoints);
            count++;
        }
        return count;
    }
}

/// <summary>
/// Status update sent from the handler to the ViewModel during/after the pick loop.
/// </summary>
public record PickLoopUpdate(int TotalCreated, int TotalFailed, bool LoopEnded, string LastStatus = null);
