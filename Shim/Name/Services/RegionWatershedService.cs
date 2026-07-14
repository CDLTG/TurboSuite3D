#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Name.Regions;
using TurboSuite.Shared.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// TurboName-1 — auto-generate room regions. <b>Revit adapter</b> over the Revit-free
/// <see cref="RegionWatershedEngine"/>: extracts walls/doors/area/seeds from the linked CAD (same extractors
/// as the manual TurboName path), clips them to the active view's crop box, converts them to Core structs,
/// runs the partition + vectorization engine, renders the dev-aid PNG from the returned grid, and converts
/// the boundary polygons back to Revit <see cref="XYZ"/>. It creates NOTHING itself — <see cref="Run"/>
/// returns the boundaries in a <see cref="WatershedResult"/>; the caller wraps them in a transaction to build
/// the FilledRegions.
///
/// The partition algorithm, its tuned constants, and the dead-ends record now live in Core
/// (<c>Core/Name/RegionWatershedEngine.cs</c>, <c>RegionVectorizer.cs</c>, <c>GapBridging.cs</c>) with oracle
/// tests in <c>Tests/</c>. Only the Revit-coupled seam — extraction, crop-box clip, XYZ↔Pt conversion, PNG
/// path — stays here.
/// </summary>
public static class RegionWatershedService
{
    /// <summary>A vectorized room territory: its seed room name + the closed boundary polygon (Revit coords).</summary>
    public sealed record GeneratedRegion(string RoomName, List<XYZ> Boundary);

    /// <summary>Diagnostics report + the vectorized boundaries the caller turns into FilledRegions.</summary>
    public sealed record WatershedResult(string Report, List<GeneratedRegion> Regions);

    /// <summary>
    /// Extracts + crops the linked-CAD geometry, runs the Core watershed/vectorize engine, and returns the
    /// boundary polygons alongside a human-readable diagnostics report. Writes a debug bitmap of the partition
    /// to the desktop (dev aid). Purely a read of the model + linked CAD — creates nothing; the caller wraps
    /// <see cref="WatershedResult.Regions"/> in a transaction to build the FilledRegions.
    /// </summary>
    public static WatershedResult Run(Document doc, View view, CadRoomSourceSettings settings)
    {
        var sb = new StringBuilder();

        // ── Pull inputs (same extractors as the manual TurboName path) ──
        var (walls, doors, area) =
            CadWallExtractorService.ExtractWallGeometry(doc, view, settings);
        var rooms = CadRoomExtractorService.ExtractRoomData(doc, view, settings)
            .Where(r => !string.IsNullOrWhiteSpace(r.RoomName))
            .ToList();

        sb.AppendLine($"Raw: walls {walls.Count}, doors {doors.Count}, area {area.Count}, seeds {rooms.Count}");
        sb.AppendLine($"Links: {CadWallExtractorService.LastLinkInfo}");
        sb.AppendLine($"Door layers: [{string.Join(", ", settings.DoorLayerNames ?? new List<string>())}]  ({CadWallExtractorService.LastDoorLayerInfo})");

        // ── Crop-box clip: isolate this floor from a multi-floor stacked DWG ──
        if (view.CropBoxActive)
        {
            var (minX, minY, maxX, maxY) = CropAabb(view);
            walls = walls.Where(s => SegInBox(s.StartPoint, s.EndPoint, minX, minY, maxX, maxY)).ToList();
            area = area.Where(s => SegInBox(s.StartPoint, s.EndPoint, minX, minY, maxX, maxY)).ToList();
            doors = doors.Where(p => PointInBox(p, minX, minY, maxX, maxY)).ToList();
            rooms = rooms.Where(r => PointInBox(r.RevitPoint, minX, minY, maxX, maxY)).ToList();
            sb.AppendLine($"Crop-clipped: walls {walls.Count}, doors {doors.Count}, area {area.Count}, seeds {rooms.Count}");
        }
        else
        {
            sb.AppendLine("Crop box NOT active — no floor isolation (enable + size the view crop).");
        }

        if (rooms.Count == 0)
            return new WatershedResult(sb.AppendLine("\nNo seeds after clipping — nothing to partition.").ToString(),
                new List<GeneratedRegion>());

        // ── Convert to Core structs (drop Z; the pipeline is planar) and run the Revit-free engine ──
        var coreWalls = walls.Select(ToWallSeg).ToList();
        var coreArea = area.Select(ToWallSeg).ToList();
        var coreDoors = doors.Select(ToPt).ToList();
        var coreSeeds = rooms.Select(r => new Seed(ToPt(r.RevitPoint), r.RoomName)).ToList();

        var output = RegionWatershedEngine.Run(coreWalls, coreDoors, coreArea, coreSeeds);
        sb.Append(output.Report);

        // ── Debug image (dev aid) — rendered from the grid the engine returned ──
        if (output.Grid != null)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TurboName_watershed.png");
                RegionDebugImage.ExportPng(output.Grid, output.Width, output.Height, path, output.Markers);
                sb.AppendLine($"Image: {path}");
                sb.AppendLine("  legend: black=wall  magenta=envelope  blue=door-seal(tight)  yellow=door-seal(loose)  orange=proximity-bridge");
                sb.AppendLine("  proximity bridges are ALSO ringed with an orange box marker so they stand out.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Bitmap export failed: {ex.Message}");
            }
        }

        var regions = output.Regions
            .Select(r => new GeneratedRegion(r.RoomName, r.Boundary.Select(ToXyz).ToList()))
            .ToList();
        return new WatershedResult(sb.ToString(), regions);
    }

    // ── Revit ↔ Core conversions (planar: Z dropped going in, set to 0 coming back) ──
    private static Pt ToPt(XYZ p) => new Pt(p.X, p.Y);
    private static XYZ ToXyz(Pt p) => new XYZ(p.X, p.Y, 0);
    private static WallSeg ToWallSeg(CadWallSegment s) => new WallSeg(ToPt(s.StartPoint), ToPt(s.EndPoint), s.IsVirtual);

    // Model-space AABB of the active view's crop box (8 transformed corners → XY min/max).
    private static (double minX, double minY, double maxX, double maxY) CropAabb(View view)
    {
        var cb = view.CropBox;
        var t = cb.Transform;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var local = new XYZ(
                (i & 1) == 0 ? cb.Min.X : cb.Max.X,
                (i & 2) == 0 ? cb.Min.Y : cb.Max.Y,
                (i & 4) == 0 ? cb.Min.Z : cb.Max.Z);
            var p = t.OfPoint(local);
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return (minX, minY, maxX, maxY);
    }

    private static bool PointInBox(XYZ p, double minX, double minY, double maxX, double maxY)
        => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;

    // Keep a segment if its bbox overlaps the crop bbox.
    private static bool SegInBox(XYZ a, XYZ b, double minX, double minY, double maxX, double maxY)
    {
        double sMinX = Math.Min(a.X, b.X), sMaxX = Math.Max(a.X, b.X);
        double sMinY = Math.Min(a.Y, b.Y), sMaxY = Math.Max(a.Y, b.Y);
        return sMinX <= maxX && sMaxX >= minX && sMinY <= maxY && sMaxY >= minY;
    }
}
