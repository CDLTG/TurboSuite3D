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
/// Auto-generate room regions. <b>Revit adapter</b> over the Revit-free
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
    /// <summary>
    /// A vectorized room territory: its seed room name + the closed boundary polygon (Revit coords), plus the
    /// interior <see cref="Seed"/> point it flooded out from — see <see cref="GenRegion.Seed"/> for why that
    /// is the seeded pixel's centre and not the raw CAD label location.
    /// </summary>
    public sealed record GeneratedRegion(string RoomName, List<XYZ> Boundary, XYZ Seed);

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
        // Same CropScope the clear planner uses, so "which floor is this?" has exactly one answer.
        var crop = CropScope.For(view);
        if (crop.IsActive)
        {
            walls = walls.Where(s => crop.OverlapsSegment(s.StartPoint, s.EndPoint)).ToList();
            area = area.Where(s => crop.OverlapsSegment(s.StartPoint, s.EndPoint)).ToList();
            doors = doors.Where(crop.Contains).ToList();
            rooms = rooms.Where(r => crop.Contains(r.RevitPoint)).ToList();
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

#if DEBUG
        // ── Debug image (dev aid) — rendered from the grid the engine returned. DEBUG-only by design: the
        //    only path to production is publish.ps1's Release build (see PUBLISHING.md), so this is compiled
        //    out of shipped binaries automatically — no runtime flag to remember. Keep it for iterating on the
        //    partition; if it's ever wanted from a Release build, swap to an env-var opt-in instead. ──
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
#endif

        var regions = output.Regions
            .Select(r => new GeneratedRegion(r.RoomName, r.Boundary.Select(ToXyz).ToList(), ToXyz(r.Seed)))
            .ToList();
        return new WatershedResult(sb.ToString(), regions);
    }

    // ── Revit ↔ Core conversions (planar: Z dropped going in, set to 0 coming back) ──
    private static Pt ToPt(XYZ p) => new Pt(p.X, p.Y);
    private static XYZ ToXyz(Pt p) => new XYZ(p.X, p.Y, 0);
    private static WallSeg ToWallSeg(CadWallSegment s) => new WallSeg(ToPt(s.StartPoint), ToPt(s.EndPoint), s.IsVirtual);

}
