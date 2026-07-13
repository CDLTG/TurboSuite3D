#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Shared.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// TurboName-1 — auto-generate room regions via a raster competitive watershed seeded by CAD room labels,
/// bounded by a hard building-envelope (Area layer) barrier.
///
/// This is the productized home of the validated TurboSpike watershed (see
/// <c>Specs/TurboName-Region-Generation-Plan.md</c> §10 — the canonical reference implementation). It runs
/// the partition, emits diagnostics (leaks / collision px / doors sealed, plus a debug bitmap), and
/// vectorizes each room territory to a boundary polygon (<see cref="RegionVectorizer"/>). It creates NOTHING
/// itself — <see cref="Run"/> returns the boundaries in a <see cref="WatershedResult"/>; the caller wraps
/// them in a transaction to build the FilledRegions.
///
/// Pipeline (order matters): seeds from CAD labels → crop-box clip → door sealing (block-agnostic,
/// gap-in-nearest-wall) → thin-wall raster (NO dilation) with Area envelope as a hard exterior barrier →
/// multi-source competitive BFS (exterior = owner 1 seeded on the border ring; one room per label) →
/// vectorize each owner's raster territory (contour → Douglas-Peucker → wall-snap).
/// </summary>
public static class RegionWatershedService
{
    // ── Tuned constants (do not re-derive — see plan §2) ──
    private const double TargetPixelsPerFoot = 12.0;   // 1"/px raster resolution
    private const long GridPixelCap = 50_000_000;      // ~200 MB int[]; scales ppf down past this
    private const double EnvelopePadFt = 2.0;          // raster bounds padding

    private const double DoorDedupDist = 2.0;   // ft — collapse many-entities-per-door into one opening
    private const double DoorWallSearch = 6.0;  // ft — how far from a door marker to look for its wall
    private const double MinWallLen = 2.0;      // ft — ignore short jamb returns when picking the ref wall
    private const double CollinearPerp = 0.75;  // ft — perpendicular tol for "segment is on this wall's line"
    private const double MaxDoorWidth = 12.0;   // ft — gaps wider than this are real openings, not doors

    private const double LeakAreaSqFt = 3000.0; // territories bigger than this are exterior/room leaks
    private const double MinRegionSqFt = 4.0;   // territories smaller than this are noise — not vectorized

    // Door sealing now complements the priority-flood watershed rather than carrying it. Targeted policy:
    // apply only TIGHT seals (door marker inside a real wall gap) — these pin cuts at real doors and seal
    // room→unseeded (closet/chase) openings the flood can't. LOOSE seals (wrong perpendicular wall, ~half
    // spurious) are dropped; the flood handles any real doors among them.
    private const bool EnableDoorSealing = true;
    private const bool ApplyLooseDoorSeals = false;

    // Grid cell values: >=0 owners (0 = Free/unreached, 1 = EXTERIOR, 2.. = rooms); <0 barriers.
    // Each barrier source gets its own value so the debug bitmap can color them distinctly. All <0 block.
    private const int Free = 0;
    private const int Exterior = 1;
    private const int FirstRoomOwner = 2;
    private const int Wall = -1;             // real CAD wall
    private const int EnvWall = -3;          // Area-layer building envelope
    private const int DoorSeal = -4;         // door seal, marker INSIDE the sealed gap (trustworthy)
    private const int ProximityBridge = -6;  // GapBridgingService — corner gap close (≤1 ft)
    private const int DoorSealLoose = -7;    // door seal, marker OUTSIDE the gap (suspect — wrong ref wall)
    private const int SlotFill = -8;         // sealed thin channel (pocket-door cavity / chase)

    // Thin-slot sealing (pocket doors etc.). Fill any free pixel walled on both sides within this reach.
    private const bool SealThinSlots = true;
    private const int SlotWidthPx = 4;       // ~4" at 12 px/ft — cavity narrower than this is not room.
                                             // Kept at 4: bumping to 8 (to seal a 7.25" orthogonal poché
                                             // cavity) frayed DIAGONAL wall cavities floor-wide — the
                                             // axis-aligned scan can't span a diagonal cavity (~1.41× wider
                                             // along-axis), so it seals the center and leaves triangular
                                             // fringe tabs the region inherits. A single mis-drawn room
                                             // (LIVING GUEST) is better handled as a named "Needs manual"
                                             // skip than by degrading every diagonal room.

    /// <summary>A vectorized room territory: its seed room name + the closed boundary polygon.</summary>
    public sealed record GeneratedRegion(string RoomName, List<XYZ> Boundary);

    /// <summary>Diagnostics report + the vectorized boundaries the caller turns into FilledRegions.</summary>
    public sealed record WatershedResult(string Report, List<GeneratedRegion> Regions);

    /// <summary>
    /// Runs the full watershed, vectorizes each room territory to a boundary polygon, and returns those
    /// boundaries alongside a human-readable diagnostics report. Writes a debug bitmap of the partition to
    /// the desktop (dev aid — mirrors the spike). Purely a read of the model + linked CAD — creates nothing;
    /// the caller wraps <see cref="WatershedResult.Regions"/> in a transaction to build the FilledRegions.
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
            return Empty(sb.AppendLine("\nNo seeds after clipping — nothing to partition."));

        // ── Gap-close: proximity-only (≤1 ft) corner bridges. Collinear + door bridging were removed —
        //    the priority-flood watershed self-cuts doorless room-to-room gaps, and door openings are handled
        //    by SealDoorsAlongWalls below. Proximity can only span ≤1 ft, so it can't wall off an opening. ──
        var realWalls = walls; // extractor walls are all real (non-virtual)
        GapBridgingService.BridgeProximityGaps(realWalls, out var proximityBridges);

        // Explicit per-bridge dump so the effect is visible/quantifiable, not just a count. Each line is the
        // bridge midpoint in project feet + its span length — cross-reference against the marked-up PNG.
        sb.AppendLine($"Proximity bridges (orange): {proximityBridges.Count}");
        foreach (var b in proximityBridges.OrderByDescending(s => (s.EndPoint - s.StartPoint).GetLength()))
        {
            var m = (b.StartPoint + b.EndPoint) * 0.5;
            sb.AppendLine($"    ({m.X:F1}, {m.Y:F1})  len {(b.EndPoint - b.StartPoint).GetLength():F2} ft");
        }

        // ── Door sealing: block-agnostic wall-gap seal at each door marker. (TEMP: gated off for the
        //    priority-flood experiment — see EnableDoorSealing.) ──
        List<CadWallSegment> tightDoorSeals, looseDoorSeals;
        if (EnableDoorSealing)
        {
            SealDoorsAlongWalls(realWalls, doors, out tightDoorSeals, out looseDoorSeals,
                out int sealedCount, out int doorClusters);
            if (!ApplyLooseDoorSeals) looseDoorSeals = new List<CadWallSegment>(); // targeted: tight only
            sb.AppendLine($"Doors sealed: {sealedCount}/{doorClusters}  " +
                $"({tightDoorSeals.Count} tight applied / {(ApplyLooseDoorSeals ? "loose applied" : "loose dropped")})  " +
                $"({GapBridgingService.LastBridgeInfo})");
        }
        else
        {
            tightDoorSeals = new List<CadWallSegment>();
            looseDoorSeals = new List<CadWallSegment>();
            sb.AppendLine($"Door sealing DISABLED (priority-flood experiment)  ({GapBridgingService.LastBridgeInfo})");
        }

        // ── Raster bounds over all barrier geometry (+pad) ──
        double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;
        void Extend(XYZ p)
        {
            bMinX = Math.Min(bMinX, p.X); bMinY = Math.Min(bMinY, p.Y);
            bMaxX = Math.Max(bMaxX, p.X); bMaxY = Math.Max(bMaxY, p.Y);
        }
        foreach (var s in realWalls) { Extend(s.StartPoint); Extend(s.EndPoint); }
        foreach (var s in area) { Extend(s.StartPoint); Extend(s.EndPoint); }
        foreach (var s in tightDoorSeals) { Extend(s.StartPoint); Extend(s.EndPoint); }
        foreach (var s in looseDoorSeals) { Extend(s.StartPoint); Extend(s.EndPoint); }
        if (bMinX > bMaxX)
            return Empty(sb.AppendLine("\nNo wall/area geometry to rasterize."));

        bMinX -= EnvelopePadFt; bMinY -= EnvelopePadFt;
        bMaxX += EnvelopePadFt; bMaxY += EnvelopePadFt;
        double widthFt = bMaxX - bMinX, heightFt = bMaxY - bMinY;

        // ── Resolution: cap total pixels, scaling ppf down if the floor is enormous ──
        double ppf = TargetPixelsPerFoot;
        if (widthFt * heightFt * ppf * ppf > GridPixelCap)
            ppf = Math.Sqrt(GridPixelCap / (widthFt * heightFt));
        int w = (int)Math.Ceiling(widthFt * ppf) + 1;
        int h = (int)Math.Ceiling(heightFt * ppf) + 1;

        int Px(double x) => (int)Math.Round((x - bMinX) * ppf);
        int Py(double y) => (int)Math.Round((y - bMinY) * ppf);

        var grid = new int[(long)w * h > int.MaxValue ? 0 : w * h];
        if (grid.Length == 0)
            return Empty(sb.AppendLine($"\nGrid too large ({w}×{h}). Tighten the crop box."));
        sb.AppendLine($"Grid: {w}×{h} @ {ppf:F1} px/ft  ({widthFt:F0}×{heightFt:F0} ft)");

        // ── Rasterize barriers (dilation = 0). Real walls are drawn first and win where any other
        //    barrier crosses them (RasterLine guards against overwriting a real Wall). ──
        void Raster(IEnumerable<CadWallSegment> segs, int val)
        {
            foreach (var s in segs)
                RasterLine(grid, w, h, Px(s.StartPoint.X), Py(s.StartPoint.Y),
                    Px(s.EndPoint.X), Py(s.EndPoint.Y), val);
        }
        Raster(realWalls, Wall);
        Raster(area, EnvWall);
        Raster(tightDoorSeals, DoorSeal);
        Raster(looseDoorSeals, DoorSealLoose);
        Raster(proximityBridges, ProximityBridge);

        int[] dxs = { 1, -1, 0, 0 };
        int[] dys = { 0, 0, 1, -1 };

        // ── Seal thin slots (pocket-door cavities, chases): a free pixel walled on BOTH sides within
        //    SlotWidthPx along either axis is a narrow channel the flood shouldn't enter — flooding it
        //    breeds a needle-thin finger that vectorizes into a self-touching loop FilledRegion rejects.
        //    Doorway throats (~2.5–3 ft) and open rooms are far wider, so they're untouched. ──
        if (SealThinSlots)
        {
            bool WallWithin(int x, int y, int dx, int dy)
            {
                for (int s = 1; s <= SlotWidthPx; s++)
                {
                    int nx = x + dx * s, ny = y + dy * s;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) return false;
                    if (grid[ny * w + nx] < 0) return true;
                }
                return false;
            }
            var slotPix = new List<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (grid[y * w + x] != Free) continue;
                    bool vert = WallWithin(x, y, 0, -1) && WallWithin(x, y, 0, 1);
                    bool horz = WallWithin(x, y, -1, 0) && WallWithin(x, y, 1, 0);
                    if (vert || horz) slotPix.Add(y * w + x);
                }
            foreach (int i in slotPix) grid[i] = SlotFill;
            sb.AppendLine($"Thin slots sealed: {slotPix.Count} px (≤{SlotWidthPx}\" channels)");
        }

        // ── Distance transform: each free pixel's 4-connected hop distance to the nearest barrier. This is
        //    the relief the priority flood floods over — doorway throats are local minima (walls pinch the
        //    free space), so they get filled LAST and the basin boundary snaps to them. ──
        var dist = new int[w * h];
        for (int i = 0; i < grid.Length; i++) dist[i] = grid[i] < 0 ? 0 : -1;
        var dq = new Queue<int>();
        for (int i = 0; i < grid.Length; i++) if (grid[i] < 0) dq.Enqueue(i);
        int maxDist = 0;
        while (dq.Count > 0)
        {
            int idx = dq.Dequeue();
            int x = idx % w, y = idx / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dxs[k], ny = y + dys[k];
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int ni = ny * w + nx;
                if (dist[ni] != -1) continue;
                dist[ni] = dist[idx] + 1;
                if (dist[ni] > maxDist) maxDist = dist[ni];
                dq.Enqueue(ni);
            }
        }
        for (int i = 0; i < dist.Length; i++) if (dist[i] < 0) dist[i] = 0; // unreachable (no walls) — treat as throat

        // ── Priority-flood watershed. Max-heap keyed by distance-to-wall packed into the high bits; expand
        //    the highest-distance (most open) frontier pixel first, claim-on-push. Owners fill their open
        //    interiors first and creep into narrow throats last, meeting there. ──
        var heap = new List<long>();
        void HeapPush(int d, int idx)
        {
            heap.Add(((long)d << 32) | (uint)idx);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p] >= heap[i]) break;
                (heap[p], heap[i]) = (heap[i], heap[p]);
                i = p;
            }
        }
        int HeapPop()
        {
            long root = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0, n = heap.Count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, big = i;
                if (l < n && heap[l] > heap[big]) big = l;
                if (r < n && heap[r] > heap[big]) big = r;
                if (big == i) break;
                (heap[big], heap[i]) = (heap[i], heap[big]);
                i = big;
            }
            return (int)(root & 0xFFFFFFFF);
        }

        // Exterior = owner 1, seeded on the whole border ring (every free border pixel).
        void SeedBorder(int x, int y)
        {
            int i = y * w + x;
            if (grid[i] == Free) { grid[i] = Exterior; HeapPush(dist[i], i); }
        }
        for (int x = 0; x < w; x++) { SeedBorder(x, 0); SeedBorder(x, h - 1); }
        for (int y = 0; y < h; y++) { SeedBorder(0, y); SeedBorder(w - 1, y); }

        // One room per label; spiral to nearest free pixel if a label lands on a wall.
        var ownerName = new Dictionary<int, string>();
        int owner = FirstRoomOwner;
        int seededRooms = 0, skippedRooms = 0;
        int spiralRadius = (int)Math.Ceiling(ppf * 2);
        foreach (var room in rooms)
        {
            int sx = Px(room.RevitPoint.X), sy = Py(room.RevitPoint.Y);
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) { skippedRooms++; continue; }
            int si = sy * w + sx;
            if (grid[si] != Free)
            {
                si = NearestFree(grid, w, h, sx, sy, spiralRadius);
                if (si < 0) { skippedRooms++; continue; }
            }
            grid[si] = owner;
            ownerName[owner] = room.RoomName;
            HeapPush(dist[si], si);
            owner++;
            seededRooms++;
        }
        sb.AppendLine($"Distance transform: max {maxDist} px ({maxDist / ppf:F1} ft to wall)");

        long collisions = 0;
        while (heap.Count > 0)
        {
            int idx = HeapPop();
            int own = grid[idx];
            int x = idx % w, y = idx / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dxs[k], ny = y + dys[k];
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int ni = ny * w + nx;
                int nv = grid[ni];
                if (nv < 0) continue;               // barrier
                if (nv == Free) { grid[ni] = own; HeapPush(dist[ni], ni); }
                else if (nv != own) collisions++;   // two owners meet — throat cut
            }
        }

        // ── Diagnostics: per-owner area, leaks, collisions ──
        var pxCount = new Dictionary<int, long>();
        foreach (int v in grid)
            if (v >= FirstRoomOwner) { pxCount.TryGetValue(v, out long c); pxCount[v] = c + 1; }

        double sqftPerPx = 1.0 / (ppf * ppf);
        var leaks = pxCount.Where(kv => kv.Value * sqftPerPx > LeakAreaSqFt)
            .OrderByDescending(kv => kv.Value).ToList();

        sb.AppendLine();
        sb.AppendLine($"Rooms partitioned: {pxCount.Count}/{seededRooms} seeded ({skippedRooms} skipped)");
        sb.AppendLine($"Leaks (>{LeakAreaSqFt:F0} sqft): {leaks.Count}");
        foreach (var kv in leaks.Take(5))
            sb.AppendLine($"    {ownerName.GetValueOrDefault(kv.Key, "?")}: {kv.Value * sqftPerPx:F0} sqft");
        sb.AppendLine($"Collision px: {collisions}");

        // ── Vectorize each room territory → boundary polygon. Skip leaks (partition failed there) and
        //    sub-noise slivers; the rest become FilledRegions in the caller's transaction. ──
        var regions = new List<GeneratedRegion>();
        var leakOwners = new HashSet<int>(leaks.Select(kv => kv.Key));
        XYZ ToRevit(int px, int py) => new XYZ(px / ppf + bMinX, py / ppf + bMinY, 0);
        int vectorized = 0, vecSkipped = 0;
        var traceFails = new List<string>();   // real rooms whose boundary wouldn't vectorize — named for manual redraw
        foreach (var kv in pxCount.OrderBy(k => k.Key))
        {
            int own = kv.Key;
            if (leakOwners.Contains(own) || kv.Value * sqftPerPx < MinRegionSqFt) { vecSkipped++; continue; }
            var boundary = RegionVectorizer.Trace(grid, w, h, own, kv.Value, ToRevit, realWalls, out string why);
            if (boundary == null || boundary.Count < 3)
            {
                vecSkipped++;
                // A named, above-noise territory that wouldn't vectorize = a mis-drawn room (leak/self-touch).
                // Surface it — with the failure reason + location — so the user knows which room to draw by
                // hand and we can categorize the failure mode without guessing.
                var nm = ownerName.GetValueOrDefault(own, "");
                nm = string.IsNullOrWhiteSpace(nm) ? "(unnamed)" : nm;
                traceFails.Add(string.IsNullOrWhiteSpace(why) ? nm : $"{nm} — {why}");
                continue;
            }
            regions.Add(new GeneratedRegion(ownerName.GetValueOrDefault(own, ""), boundary));
            vectorized++;
        }
        sb.AppendLine($"Vectorized: {vectorized} region(s)  ({vecSkipped} skipped: leak/noise/trace-fail)");
        if (traceFails.Count > 0)
            sb.AppendLine($"Needs manual ({traceFails.Count}):\n    " + string.Join("\n    ", traceFails));

        // ── Debug image (dev aid) ──
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TurboName_watershed.png");

            // Marker boxes so proximity bridges are unmissable even where their thin line hides under a wall.
            var markers = new List<(int px, int py, byte r, byte g, byte b)>();
            foreach (var s in proximityBridges)
            {
                var m = (s.StartPoint + s.EndPoint) * 0.5;
                markers.Add((Px(m.X), Py(m.Y), 255, 150, 0)); // orange
            }

            ExportPng(grid, w, h, path, markers);
            sb.AppendLine($"Image: {path}");
            sb.AppendLine("  legend: black=wall  magenta=envelope  blue=door-seal(tight)  yellow=door-seal(loose)  orange=proximity-bridge");
            sb.AppendLine("  proximity bridges are ALSO ringed with an orange box marker so they stand out.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Bitmap export failed: {ex.Message}");
        }

        return new WatershedResult(sb.ToString(), regions);
    }

    private static WatershedResult Empty(StringBuilder sb) =>
        new WatershedResult(sb.ToString(), new List<GeneratedRegion>());

    // ── Block-agnostic door sealing (verbatim from plan §10) ──
    // Door marker LOCATES the opening; the seal is the GAP in the nearest long wall's own line.
    // Diagnostic split: <paramref name="tightSeals"/> = door marker falls INSIDE the sealed gap (trustworthy);
    // <paramref name="looseSeals"/> = door merely nearest an incidental gap it's OUTSIDE of (suspect — likely a
    // perpendicular/wrong reference wall). Both are still returned as barriers; the split is for coloring only.
    private static void SealDoorsAlongWalls(
        List<CadWallSegment> realWalls, List<XYZ> doorPositions,
        out List<CadWallSegment> tightSeals, out List<CadWallSegment> looseSeals,
        out int sealedCount, out int clusters)
    {
        tightSeals = new List<CadWallSegment>();
        looseSeals = new List<CadWallSegment>();
        var pts = new List<XYZ>();
        foreach (var p in doorPositions)
            if (!pts.Any(q => q.DistanceTo(p) < DoorDedupDist)) pts.Add(p); // dedup many-entities-per-door
        clusters = pts.Count;
        sealedCount = 0;

        foreach (var door in pts)
        {
            // Nearest LONG wall segment defines the line the door sits on.
            CadWallSegment refSeg = null;
            double best = DoorWallSearch;
            foreach (var s in realWalls)
            {
                if ((s.EndPoint - s.StartPoint).GetLength() < MinWallLen) continue;
                double d = PointToSeg(door, s.StartPoint, s.EndPoint);
                if (d < best) { best = d; refSeg = s; }
            }
            if (refSeg == null) continue;
            var origin = refSeg.StartPoint;
            var dir = (refSeg.EndPoint - refSeg.StartPoint).Normalize();

            // Covered spans = projections of every wall segment lying on this line.
            var covered = new List<(double a, double b)>();
            foreach (var s in realWalls)
            {
                if (Perp(s.StartPoint, origin, dir) > CollinearPerp) continue;
                if (Perp(s.EndPoint, origin, dir) > CollinearPerp) continue;
                double t0 = Proj(s.StartPoint, origin, dir), t1 = Proj(s.EndPoint, origin, dir);
                covered.Add((Math.Min(t0, t1), Math.Max(t0, t1)));
            }
            if (covered.Count < 2) continue;
            covered.Sort((x, y) => x.a.CompareTo(y.a));
            var merged = new List<(double a, double b)>();
            foreach (var c in covered)
            {
                if (merged.Count > 0 && c.a <= merged[^1].b + 0.01)
                { var m = merged[^1]; merged[^1] = (m.a, Math.Max(m.b, c.b)); }
                else merged.Add(c);
            }

            // Gap between consecutive covered spans, containing or nearest the door marker.
            double tp = Proj(door, origin, dir);
            double gA = 0, gB = 0, bestDist = double.MaxValue; bool found = false;
            for (int i = 0; i + 1 < merged.Count; i++)
            {
                double g0 = merged[i].b, g1 = merged[i + 1].a, width = g1 - g0;
                if (width <= 0 || width > MaxDoorWidth) continue;
                double dist = (tp >= g0 && tp <= g1) ? 0 : Math.Min(Math.Abs(tp - g0), Math.Abs(tp - g1));
                if (dist < bestDist) { bestDist = dist; gA = g0; gB = g1; found = true; }
            }
            if (!found || bestDist > MaxDoorWidth) continue;

            var a = new XYZ(origin.X + dir.X * gA, origin.Y + dir.Y * gA, 0);
            var b = new XYZ(origin.X + dir.X * gB, origin.Y + dir.Y * gB, 0);
            var seal = new CadWallSegment(a, b, IsVirtual: true);
            if (bestDist <= 0.001) tightSeals.Add(seal); // door projects inside the gap
            else looseSeals.Add(seal);                    // door merely nearest an incidental gap
            sealedCount++;
        }
    }

    private static double Perp(XYZ q, XYZ o, XYZ dir)   // perpendicular distance of q from line (o,dir)
    { double vx = q.X - o.X, vy = q.Y - o.Y; return Math.Abs(vx * dir.Y - vy * dir.X); }

    private static double Proj(XYZ q, XYZ o, XYZ dir)   // param of q projected onto (o,dir)
    { return (q.X - o.X) * dir.X + (q.Y - o.Y) * dir.Y; }

    private static double PointToSeg(XYZ p, XYZ a, XYZ b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        if (l2 < 1e-9) return p.DistanceTo(a);
        double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
        double px = a.X + t * dx, py = a.Y + t * dy;
        return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }

    // Bresenham barrier (8-connected → watertight with 4-connected flood). Guard: never overwrite a real
    // Wall with a virtual/env barrier where they cross.
    private static void RasterLine(int[] g, int w, int h, int x0, int y0, int x1, int y1, int val)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx - dy;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h && g[y0 * w + x0] != Wall) g[y0 * w + x0] = val;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    // Spiral outward from (sx,sy) for a Free pixel (used when a room label lands on a wall). -1 if none.
    private static int NearestFree(int[] g, int w, int h, int sx, int sy, int maxR)
    {
        for (int r = 1; r <= maxR; r++)
        {
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring only
                    int nx = sx + dx, ny = sy + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    if (g[ny * w + nx] == Free) return ny * w + nx;
                }
        }
        return -1;
    }

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

    // Minimal 24-bit RGB PNG writer — self-contained (no System.Drawing). PNG so the image can be read
    // directly by tooling. Top-down rows; grid row h-1 (max Y) is drawn on top.
    private static void ExportPng(int[] grid, int w, int h, string path,
        List<(int px, int py, byte r, byte g, byte b)> markers = null)
    {
        int stride = 1 + w * 3;
        // Raw scanlines: each prefixed with filter byte 0 (none), then RGB triples.
        var raw = new byte[h * stride];
        int p = 0;
        for (int r = 0; r < h; r++)
        {
            raw[p++] = 0; // filter: none
            int gy = h - 1 - r;
            for (int x = 0; x < w; x++)
            {
                (byte rr, byte gg, byte bb) = Color(grid[gy * w + x]);
                raw[p++] = rr; raw[p++] = gg; raw[p++] = bb;
            }
        }

        // Overlay hollow box markers (grid coords → flipped output rows). Debug-viz only; the algorithm grid
        // is untouched, so markers never act as barriers.
        if (markers != null)
        {
            const int rad = 5; // box half-size in px
            void Put(int gx, int gy, byte cr, byte cg, byte cb)
            {
                if (gx < 0 || gx >= w || gy < 0 || gy >= h) return;
                int row = h - 1 - gy;               // grid Y is bottom-up; PNG rows are top-down
                int off = row * stride + 1 + gx * 3;
                raw[off] = cr; raw[off + 1] = cg; raw[off + 2] = cb;
            }
            foreach (var (mx, my, cr, cg, cb) in markers)
                for (int d = -rad; d <= rad; d++)
                {
                    Put(mx + d, my - rad, cr, cg, cb); Put(mx + d, my + rad, cr, cg, cb); // top/bottom edges
                    Put(mx - rad, my + d, cr, cg, cb); Put(mx + rad, my + d, cr, cg, cb); // left/right edges
                }
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG signature

        var ihdr = new List<byte>();
        void Be(List<byte> d, int v)
        { d.Add((byte)(v >> 24)); d.Add((byte)(v >> 16)); d.Add((byte)(v >> 8)); d.Add((byte)v); }
        Be(ihdr, w); Be(ihdr, h);
        ihdr.Add(8);  // bit depth
        ihdr.Add(2);  // color type: truecolor RGB
        ihdr.Add(0); ihdr.Add(0); ihdr.Add(0); // compression / filter / interlace
        WriteChunk(bw, "IHDR", ihdr.ToArray());
        WriteChunk(bw, "IDAT", ZlibCompress(raw));
        WriteChunk(bw, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        bw.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        bw.Write(t);
        bw.Write(data);
        uint crc = Crc32(t, data);
        bw.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
    }

    // zlib stream = 0x78 0x9C header + raw DEFLATE + big-endian Adler-32 of the uncompressed data.
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        uint a = Adler32(raw);
        ms.WriteByte((byte)(a >> 24)); ms.WriteByte((byte)(a >> 16));
        ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static uint[] _crcTable;
    private static uint Crc32(byte[] a, byte[] b)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFF;
        foreach (byte d in a) crc = _crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        foreach (byte d in b) crc = _crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static (byte, byte, byte) Color(int v)
    {
        switch (v)
        {
            case Wall: return (0, 0, 0);              // black — real CAD wall
            case EnvWall: return (255, 0, 255);       // magenta — building envelope
            case DoorSeal: return (0, 120, 255);      // blue — door seal, marker inside gap (trustworthy)
            case DoorSealLoose: return (255, 230, 0); // yellow — door seal, marker outside gap (suspect)
            case ProximityBridge: return (255, 150, 0); // orange — proximity (corner) gap-bridge
            case SlotFill: return (0, 200, 120);      // green — sealed thin slot (pocket-door cavity)
            case Free: return (255, 255, 255);        // white — unreached
            case Exterior: return (210, 210, 210);    // light gray — exterior
            default:                                   // room — hashed distinct color
                byte r = (byte)(60 + (v * 67) % 190);
                byte g = (byte)(60 + (v * 133) % 190);
                byte b = (byte)(60 + (v * 199) % 190);
                return (r, g, b);
        }
    }
}
