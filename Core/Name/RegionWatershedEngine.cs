#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TurboSuite.Name.Regions
{
    /// <summary>
    /// TurboName-1 — auto-generate room regions via a raster competitive watershed seeded by CAD room labels,
    /// bounded by a hard building-envelope (Area layer) barrier. This is the Revit-free core: plain-struct in
    /// (<see cref="WallSeg"/> walls/area, <see cref="Pt"/> door markers, <see cref="Seed"/> room seeds), plain
    /// boundaries out (<see cref="GenRegion"/>). The Shim adapter (RegionWatershedService) does the Revit
    /// extraction + crop-box clip, converts to these structs, renders the debug PNG from the returned grid, and
    /// turns the boundaries into FilledRegions in a transaction — this engine creates nothing.
    ///
    /// Pipeline (order matters): proximity gap-close → door sealing (block-agnostic, gap-in-nearest-wall) →
    /// thin-wall raster (NO dilation) with Area envelope as a hard exterior barrier → thin-slot seal (pocket
    /// doors) → distance-transform priority-flood watershed (exterior = owner 1 on the border ring; one room per
    /// seed) → needle-finger trim → vectorize each owner's territory (<see cref="RegionVectorizer"/>).
    ///
    /// Dead ends — DO NOT revisit (each burned real time before we landed on raster-watershed):
    ///  • Planar-graph / face-tracing (9 rounds): CAD walls are ~148 disconnected components whose endpoints
    ///    don't meet within tolerance — fatal for graph topology, a non-issue in raster (touching pixels meet).
    ///  • Single-click raster flood-fill: no notion of separate rooms; superseded by the seeded watershed.
    ///  • Jamb-to-jamb endpoint pairing to seal doors: DESTRUCTIVE — walls off unreachable pockets. The
    ///    collinearity constraint in SealDoorsAlongWalls (seal the gap in the wall's OWN line) is what makes
    ///    the wall-gap version safe.
    ///  • Parsing door-block anatomy (swing-arc radius / rotation): blocks are anonymous, multi-visibility,
    ///    architect-specific — stay block-agnostic (the marker only LOCATES; the wall line defines the seal).
    ///  • Window layer for exterior sealing: removed model-wide — the Area envelope solves it better.
    ///  • Into-wall overshoot (grow regions ~0.5" into walls so a wall-snapped keypad falls inside):
    ///    unnecessary AND harmful. The downstream containment (Shared/Services/RegionRoomLookupService)
    ///    already nudges a face-snapped point ~3/8" inward before rejecting it, so a face-snapped element is
    ///    already claimed; and a flat outward edge offset overlapped adjacent rooms inside shared walls
    ///    (hundreds of Revit "overlapping region" warnings). If coverage ever falls short, widen that nudge —
    ///    never overshoot region geometry.
    ///  • Acceptance / confidence gate (reject foreign-seed / unbacked-perimeter territories before create):
    ///    not needed — the watershed already yields 0 leaks, so clean-or-skip covers it without the tuning risk.
    /// (Collinear + door-opening gap-bridging dead ends are documented on <see cref="GapBridging"/>;
    /// in-place jog repair + the resolution bump are documented on <see cref="RegionVectorizer"/>.)
    /// </summary>
    public static class RegionWatershedEngine
    {
        // ── Tuned constants (do not re-derive — each value's rationale is inline below / in git history) ──
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
        private const int NeedleReachPx = 10;       // ~10" @12px/ft. Per-side scan for the needle-finger trim: a
                                                    // room pixel is a needle iff its own-owner run exits to *another
                                                    // room owner* within this many px on BOTH sides (thin, flanked by
                                                    // rooms not walls). Catches fingers up to ~2× this wide; a barrier
                                                    // flank (a real narrow closet) or a full-width side (a normal
                                                    // seam) is never flagged, so nothing legitimate is touched.

        // Door sealing complements the priority-flood watershed rather than carrying it. Targeted policy:
        // apply only TIGHT seals (door marker inside a real wall gap) — these pin cuts at real doors and seal
        // room→unseeded (closet/chase) openings the flood can't. LOOSE seals (wrong perpendicular wall, ~half
        // spurious) are dropped; the flood handles any real doors among them. Flip to apply loose seals on a
        // sloppier floor where the tight set leaves openings.
        private const bool ApplyLooseDoorSeals = false;

        // Grid cell values: >=0 owners (0 = Free/unreached, 1 = EXTERIOR, 2.. = rooms); <0 barriers.
        // Each barrier source gets its own value so the debug bitmap can color them distinctly. All <0 block.
        internal const int Free = 0;
        internal const int Exterior = 1;
        internal const int FirstRoomOwner = 2;
        internal const int Wall = -1;             // real CAD wall
        internal const int EnvWall = -3;          // Area-layer building envelope
        internal const int DoorSeal = -4;         // door seal, marker INSIDE the sealed gap (trustworthy)
        internal const int ProximityBridge = -6;  // GapBridging — corner gap close (≤1 ft)
        internal const int DoorSealLoose = -7;    // door seal, marker OUTSIDE the gap (suspect — wrong ref wall)
        internal const int SlotFill = -8;         // sealed thin channel (pocket-door cavity / chase)

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

        /// <summary>
        /// Runs the full watershed on already-extracted, already-cropped geometry and vectorizes each room
        /// territory to a boundary polygon. Pure — no Revit, no file I/O; the caller renders the debug PNG from
        /// <see cref="WatershedOutput.Grid"/> and turns the boundaries into FilledRegions.
        /// </summary>
        public static WatershedOutput Run(
            List<WallSeg> walls, List<Pt> doors, List<WallSeg> area, List<Seed> seeds)
        {
            var sb = new StringBuilder();

            if (seeds.Count == 0)
                return Empty(sb.AppendLine("No seeds — nothing to partition."));

            // ── Gap-close: proximity-only (≤1 ft) corner bridges. Collinear + door bridging were removed —
            //    the priority-flood watershed self-cuts doorless room-to-room gaps, and door openings are handled
            //    by SealDoorsAlongWalls below. Proximity can only span ≤1 ft, so it can't wall off an opening. ──
            var realWalls = walls; // extractor walls are all real (non-virtual)
            GapBridging.BridgeProximityGaps(realWalls, out var proximityBridges, out string bridgeInfo);

            // Explicit per-bridge dump so the effect is visible/quantifiable, not just a count. Each line is the
            // bridge midpoint in project feet + its span length — cross-reference against the marked-up PNG.
            sb.AppendLine($"Proximity bridges (orange): {proximityBridges.Count}");
            foreach (var b in proximityBridges.OrderByDescending(s => (s.End - s.Start).GetLength()))
            {
                var m = (b.Start + b.End) * 0.5;
                sb.AppendLine($"    ({m.X:F1}, {m.Y:F1})  len {(b.End - b.Start).GetLength():F2} ft");
            }

            // ── Door sealing: block-agnostic wall-gap seal at each door marker (tight only; see ApplyLooseDoorSeals). ──
            SealDoorsAlongWalls(realWalls, doors, out var tightDoorSeals, out var looseDoorSeals,
                out int sealedCount, out int doorClusters);
            if (!ApplyLooseDoorSeals) looseDoorSeals = new List<WallSeg>(); // targeted: tight only
            sb.AppendLine($"Doors sealed: {sealedCount}/{doorClusters}  " +
                $"({tightDoorSeals.Count} tight applied / {(ApplyLooseDoorSeals ? "loose applied" : "loose dropped")})  " +
                $"({bridgeInfo})");

            // ── Raster bounds over all barrier geometry (+pad) ──
            double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;
            void Extend(Pt p)
            {
                bMinX = Math.Min(bMinX, p.X); bMinY = Math.Min(bMinY, p.Y);
                bMaxX = Math.Max(bMaxX, p.X); bMaxY = Math.Max(bMaxY, p.Y);
            }
            foreach (var s in realWalls) { Extend(s.Start); Extend(s.End); }
            foreach (var s in area) { Extend(s.Start); Extend(s.End); }
            foreach (var s in tightDoorSeals) { Extend(s.Start); Extend(s.End); }
            foreach (var s in looseDoorSeals) { Extend(s.Start); Extend(s.End); }
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
            void Raster(IEnumerable<WallSeg> segs, int val)
            {
                foreach (var s in segs)
                    RasterLine(grid, w, h, Px(s.Start.X), Py(s.Start.Y),
                        Px(s.End.X), Py(s.End.Y), val);
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
            foreach (var seed in seeds)
            {
                int sx = Px(seed.Point.X), sy = Py(seed.Point.Y);
                if (sx < 0 || sx >= w || sy < 0 || sy >= h) { skippedRooms++; continue; }
                int si = sy * w + sx;
                if (grid[si] != Free)
                {
                    si = NearestFree(grid, w, h, sx, sy, spiralRadius);
                    if (si < 0) { skippedRooms++; continue; }
                }
                grid[si] = owner;
                ownerName[owner] = seed.Name;
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

            // ── Trim needle fingers. A stranded/duplicate label, or a room whose flood crossed a doorless opening,
            //    leaves a thin finger of one owner poking through the gap into a neighbor — it vectorizes into a
            //    spurious thin slot region. The clean discriminator: a needle is thin because OTHER ROOM OWNERS
            //    flank it (it fingered through an opening, no wall between); a real narrow room is thin because
            //    WALLS flank it. So flag a room pixel iff its own-owner run is short (≤NeedleReachPx each way)
            //    along X or Y AND the pixel just past BOTH ends of that run is a *different room owner* — never a
            //    barrier. This never moves a normal seam (full-width territory on one side ⇒ not thin), never eats
            //    a wall-backed closet (a barrier flank ⇒ kept), and auto-keeps a natural stub at a doorway throat
            //    (the wall jambs flank the finger there). Each flagged pixel is reclaimed by the nearer flank. ──
            {
                int reach = NeedleReachPx;
                // True + nearer flank owner iff (x,y) of owner v sits in a run that exits to a *room owner* within
                // reach on BOTH sides along (dx,dy). Exit into a barrier, or no exit within reach, ⇒ not a needle.
                bool ThinBetweenRooms(int x, int y, int dx, int dy, int v, out int flank)
                {
                    flank = 0;
                    int op = 0, sp = 0, om = 0, sm = 0;
                    for (int s = 1; s <= reach; s++)
                    {
                        int nx = x + dx * s, ny = y + dy * s;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) return false;
                        int g = grid[ny * w + nx];
                        if (g != v) { op = g; sp = s; break; }
                    }
                    if (sp == 0) return false;                        // own run wider than reach on + side ⇒ not thin
                    for (int s = 1; s <= reach; s++)
                    {
                        int nx = x - dx * s, ny = y - dy * s;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) return false;
                        int g = grid[ny * w + nx];
                        if (g != v) { om = g; sm = s; break; }
                    }
                    if (sm == 0) return false;                        // wider than reach on − side ⇒ not thin
                    if (op < FirstRoomOwner || om < FirstRoomOwner) return false; // a barrier flank ⇒ wall-backed, keep
                    flank = sp <= sm ? op : om;                       // reclaim toward the nearer flanking room
                    return true;
                }
                // Decide against the frozen partition, then apply — so a reclaim can't cascade mid-scan.
                var reassign = new List<(int idx, int owner)>();
                for (int i = 0; i < grid.Length; i++)
                {
                    int v = grid[i];
                    if (v < FirstRoomOwner) continue;
                    int x = i % w, y = i / w;
                    if (ThinBetweenRooms(x, y, 1, 0, v, out int fx)) reassign.Add((i, fx));
                    else if (ThinBetweenRooms(x, y, 0, 1, v, out int fy)) reassign.Add((i, fy));
                }
                foreach (var (idx, o) in reassign) grid[idx] = o;
                sb.AppendLine($"Needle-finger trim (≤{reach * 2 * 12.0 / ppf:F0}\" between rooms): " +
                    $"{reassign.Count} px reclaimed");
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
            var regions = new List<GenRegion>();
            var leakOwners = new HashSet<int>(leaks.Select(kv => kv.Key));
            Pt ToModel(int px, int py) => new Pt(px / ppf + bMinX, py / ppf + bMinY);
            int vectorized = 0, vecSkipped = 0;
            var traceFails = new List<string>();   // real rooms whose boundary wouldn't vectorize — named for manual redraw
            foreach (var kv in pxCount.OrderBy(k => k.Key))
            {
                int own = kv.Key;
                if (leakOwners.Contains(own) || kv.Value * sqftPerPx < MinRegionSqFt) { vecSkipped++; continue; }
                var boundary = RegionVectorizer.Trace(grid, w, h, own, kv.Value, ToModel, realWalls, out string why);
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
                regions.Add(new GenRegion(ownerName.GetValueOrDefault(own, ""), boundary));
                vectorized++;
            }
            sb.AppendLine($"Vectorized: {vectorized} region(s)  ({vecSkipped} skipped: leak/noise/trace-fail)");
            if (traceFails.Count > 0)
                sb.AppendLine($"Needs manual ({traceFails.Count}):\n    " + string.Join("\n    ", traceFails));

            // Marker boxes so proximity bridges are unmissable even where their thin line hides under a wall.
            var markers = new List<(int X, int Y, byte R, byte G, byte B)>();
            foreach (var s in proximityBridges)
            {
                var m = (s.Start + s.End) * 0.5;
                markers.Add((Px(m.X), Py(m.Y), 255, 150, 0)); // orange
            }

            return new WatershedOutput(sb.ToString(), regions, grid, w, h, markers);
        }

        private static WatershedOutput Empty(StringBuilder sb) =>
            new WatershedOutput(sb.ToString(), new List<GenRegion>(), null, 0, 0, null);

        // ── Block-agnostic door sealing — the load-bearing novelty of the partition. ──
        // Door marker LOCATES the opening; the seal is the GAP in the nearest long wall's own line.
        // Diagnostic split: <paramref name="tightSeals"/> = door marker falls INSIDE the sealed gap (trustworthy);
        // <paramref name="looseSeals"/> = door merely nearest an incidental gap it's OUTSIDE of (suspect — likely a
        // perpendicular/wrong reference wall). Both are still returned as barriers; the split is for coloring only.
        private static void SealDoorsAlongWalls(
            List<WallSeg> realWalls, List<Pt> doorPositions,
            out List<WallSeg> tightSeals, out List<WallSeg> looseSeals,
            out int sealedCount, out int clusters)
        {
            tightSeals = new List<WallSeg>();
            looseSeals = new List<WallSeg>();
            var pts = new List<Pt>();
            foreach (var p in doorPositions)
                if (!pts.Any(q => q.DistanceTo(p) < DoorDedupDist)) pts.Add(p); // dedup many-entities-per-door
            clusters = pts.Count;
            sealedCount = 0;

            foreach (var door in pts)
            {
                // Nearest LONG wall segment defines the line the door sits on.
                WallSeg refSeg = null;
                double best = DoorWallSearch;
                foreach (var s in realWalls)
                {
                    if ((s.End - s.Start).GetLength() < MinWallLen) continue;
                    double d = PointToSeg(door, s.Start, s.End);
                    if (d < best) { best = d; refSeg = s; }
                }
                if (refSeg == null) continue;
                var origin = refSeg.Start;
                var dir = (refSeg.End - refSeg.Start).Normalize();

                // Covered spans = projections of every wall segment lying on this line.
                var covered = new List<(double a, double b)>();
                foreach (var s in realWalls)
                {
                    if (Perp(s.Start, origin, dir) > CollinearPerp) continue;
                    if (Perp(s.End, origin, dir) > CollinearPerp) continue;
                    double t0 = Proj(s.Start, origin, dir), t1 = Proj(s.End, origin, dir);
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

                var a = new Pt(origin.X + dir.X * gA, origin.Y + dir.Y * gA);
                var b = new Pt(origin.X + dir.X * gB, origin.Y + dir.Y * gB);
                var seal = new WallSeg(a, b, IsVirtual: true);
                if (bestDist <= 0.001) tightSeals.Add(seal); // door projects inside the gap
                else looseSeals.Add(seal);                    // door merely nearest an incidental gap
                sealedCount++;
            }
        }

        private static double Perp(Pt q, Pt o, Pt dir)   // perpendicular distance of q from line (o,dir)
        { double vx = q.X - o.X, vy = q.Y - o.Y; return Math.Abs(vx * dir.Y - vy * dir.X); }

        private static double Proj(Pt q, Pt o, Pt dir)   // param of q projected onto (o,dir)
        { return (q.X - o.X) * dir.X + (q.Y - o.Y) * dir.Y; }

        private static double PointToSeg(Pt p, Pt a, Pt b)
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
    }
}
