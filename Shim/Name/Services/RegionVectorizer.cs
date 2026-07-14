#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Turns one owner's raster territory (from the <see cref="RegionWatershedService"/> flood grid)
/// into a clean, wall-aligned boundary polygon ready for <c>FilledRegion.Create</c>. Pure geometry —
/// no Revit transaction. A pixel belongs to the owner iff <c>grid[i] == owner</c>; every other value
/// — barriers, Free, other owners — is "outside". Each watershed owner is a single 4-connected blob
/// (one seed, 4-way flood), so the outer-boundary follower captures it; interior holes are not traced (v1).
///
/// Pipeline: left-hand contour follow → Douglas-Peucker (collapses the 1"-pixel staircase, orthogonal
/// AND diagonal, into straight chords) → <see cref="AlignToWalls"/> (moves each chord onto the wall line
/// it runs parallel-and-close to, then rebuilds corners by intersecting adjacent wall lines — so corners
/// are exact and the ~1" raster inset is removed). Chords with no matching wall (room-to-room throats)
/// are left as-is.
/// </summary>
public static class RegionVectorizer
{
    private const double SimplifyTolerance = 1.5 / 12.0; // 1.5" Douglas-Peucker

    // AlignToWalls tuning.
    private const double MaxAlignDist = 4.0 / 12.0;                    // 4" — chord→wall perpendicular reach.
                                                                       // Bigger than the ~1" inset, smaller
                                                                       // than a wall's far face, so it grabs
                                                                       // the near face and never the far one.
    private const double MaxAlignAngle = 8.0 * Math.PI / 180.0;        // chord must be within 8° of the wall
                                                                       // (tight — a far endpoint of a spuriously
                                                                       // matched off-angle chord projects wildly)
    private const double MinAlignOverlap = 1.0 / 12.0;                 // 1" of shared run before a wall counts
    private const double CornerParallel = 5.0 * Math.PI / 180.0;       // below this, two lines are "the same"
    private const double MaxCornerReach = 1.0;                         // ft — reject runaway near-parallel corners

    /// <summary>
    /// Traces <paramref name="owner"/>'s territory to a closed boundary polygon (in the model
    /// coordinates produced by <paramref name="toRevit"/>), or null if it can't be vectorized.
    /// </summary>
    public static List<XYZ> Trace(int[] grid, int w, int h, int owner, long pixelCount,
        Func<int, int, XYZ> toRevit, List<CadWallSegment> walls)
        => Trace(grid, w, h, owner, pixelCount, toRevit, walls, out _);

    /// <summary>
    /// As <see cref="Trace(int[],int,int,int,long,Func{int,int,XYZ},List{CadWallSegment})"/>, but on a null
    /// return sets <paramref name="failReason"/> to why the territory couldn't vectorize (self-touch /
    /// proper-cross with the offending model-coordinate, or a degenerate stage) so the caller can name it.
    /// </summary>
    public static List<XYZ> Trace(int[] grid, int w, int h, int owner, long pixelCount,
        Func<int, int, XYZ> toRevit, List<CadWallSegment> walls, out string failReason)
    {
        failReason = null;
        var px = TraceContourPixels(grid, w, h, owner, pixelCount);
        if (px == null || px.Count < 3) { failReason = "contour < 3 pts"; return null; }

        // The follower starts at the bottom-left owner pixel — a corner. Douglas-Peucker treats the loop
        // as an open chain and pins its endpoints, so a seam on a corner splits it (a stray chamfer) and
        // spawns degenerate loops. Rotate the seam onto a straight mid-wall run first.
        RotateSeamToStraightRun(px);

        var contour = px.Select(p => toRevit(p.x, p.y)).ToList();

        var simplified = DouglasPeucker(contour, SimplifyTolerance);
        if (simplified.Count < 3) { failReason = "DP < 3 pts"; return null; }

        // Only ever return the wall-aligned polygon. If alignment leaves it self-intersecting or degenerate
        // (the signature of a mis-drawn room the flood leaked through), return null so the caller SKIPS the
        // territory and names it for manual redraw — the plain un-aligned DP polygon is NOT a usable fallback:
        // it sits ~1" off the wall face, which breaks downstream keypad/boundary-nudge on every edge.
        var aligned = Cleanup(AlignToWalls(simplified, walls));
        if (aligned.Count < 3) { failReason = "aligned < 3 pts"; return null; }
        string why = SelfIntersectionReason(aligned);
        if (why == null) return aligned;

        // The alignment folded onto itself — the signature of a mis-drawn room, or a sub-resolution wall
        // feature (a small jog / peninsula base) the 1"/px raster can't trace cleanly. Return null so the
        // caller SKIPS it and names it for manual redraw. We deliberately do NOT try to repair it in place:
        // the un-aligned DP polygon sits ~1" off the wall face (breaks §4 keypad/boundary-nudge), and jog
        // flushing/squaring only ever produced quietly-wrong regions the user had to hand-clean anyway — an
        // honest, named skip beats a created-but-flawed region ("auto-gen only ever helps"). See the plan.
        failReason = $"{why} ({aligned.Count} pts)";
        return null;
    }

    // Rotate the pixel loop in place so index 0 sits mid-run on a straight axis-aligned stretch (a window
    // of pixels sharing an x or a y), keeping the Douglas-Peucker seam off corners. No-op if none is found.
    private static void RotateSeamToStraightRun(List<(int x, int y)> pts)
    {
        int n = pts.Count;
        const int span = 4; // ~4" straight either side
        if (n < 2 * span + 2) return;

        int start = -1;
        for (int i = 0; i < n && start < 0; i++)
        {
            bool sameX = true, sameY = true;
            var c = pts[i];
            for (int k = -span; k <= span; k++)
            {
                var p = pts[((i + k) % n + n) % n];
                if (p.x != c.x) sameX = false;
                if (p.y != c.y) sameY = false;
            }
            if (sameX || sameY) start = i;
        }
        if (start <= 0) return;

        var rotated = new List<(int, int)>(n);
        for (int i = 0; i < n; i++) rotated.Add(pts[(start + i) % n]);
        pts.Clear();
        pts.AddRange(rotated);
    }

    // Left-hand-rule boundary follower over pixel centers — identical shape to
    // RasterRegionService.TraceContour, generalized from a fill-id to an owner-id.
    private static List<(int x, int y)> TraceContourPixels(int[] grid, int w, int h, int owner, long pixelCount)
    {
        // Start at the topmost, then leftmost, owner pixel.
        int startX = -1, startY = -1;
        for (int i = 0; i < grid.Length; i++)
        {
            if (grid[i] != owner) continue;
            startY = i / w;
            startX = i % w;
            break;
        }
        if (startX < 0) return null;

        var pts = new List<(int, int)>();
        int cx = startX, cy = startY;
        int dir = 0; // 0=right, 1=down, 2=left, 3=up
        int[] dxArr = { 1, 0, -1, 0 };
        int[] dyArr = { 0, 1, 0, -1 };
        long maxSteps = pixelCount * 4 + 16;
        bool started = false;

        for (long step = 0; step < maxSteps; step++)
        {
            if (started && cx == startX && cy == startY && pts.Count > 2)
                break;

            started = true;
            pts.Add((cx, cy));

            int leftDir = (dir + 3) % 4;
            if (IsOwner(grid, w, h, cx + dxArr[leftDir], cy + dyArr[leftDir], owner))
            {
                dir = leftDir;
            }
            else if (!IsOwner(grid, w, h, cx + dxArr[dir], cy + dyArr[dir], owner))
            {
                int rightDir = (dir + 1) % 4;
                if (IsOwner(grid, w, h, cx + dxArr[rightDir], cy + dyArr[rightDir], owner))
                    dir = rightDir;
                else
                    dir = (dir + 2) % 4; // reverse
            }

            cx += dxArr[dir];
            cy += dyArr[dir];
        }

        return pts;
    }

    private static bool IsOwner(int[] grid, int w, int h, int x, int y, int owner)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return false;
        return grid[y * w + x] == owner;
    }

    private static List<XYZ> DouglasPeucker(List<XYZ> points, double tolerance)
    {
        if (points.Count <= 3) return new List<XYZ>(points);

        double maxDist = 0;
        int maxIdx = 0;
        var first = points[0];
        var last = points[^1];

        for (int i = 1; i < points.Count - 1; i++)
        {
            double dist = PointToSegmentDistance(points[i], first, last);
            if (dist > maxDist)
            {
                maxDist = dist;
                maxIdx = i;
            }
        }

        if (maxDist > tolerance)
        {
            var left = DouglasPeucker(points.GetRange(0, maxIdx + 1), tolerance);
            var right = DouglasPeucker(points.GetRange(maxIdx, points.Count - maxIdx), tolerance);
            var result = new List<XYZ>(left);
            result.AddRange(right.Skip(1));
            return result;
        }

        return new List<XYZ> { first, last };
    }

    // Edge-based wall alignment. For each polygon edge, find the wall segment it runs parallel-and-close
    // to and adopt that wall's infinite line; then set each vertex to the intersection of its two incident
    // edge-lines (exact corner) — projecting onto the single line when only one edge matched, and leaving
    // the vertex untouched when neither did (room-to-room throats have no wall to snap to).
    private static List<XYZ> AlignToWalls(List<XYZ> poly, List<CadWallSegment> walls)
    {
        int n = poly.Count;
        var linePt = new XYZ[n];   // per-edge matched wall line: a point on it …
        var lineDir = new XYZ[n];  // … and its unit direction (null ⇒ edge unmatched)

        for (int i = 0; i < n; i++)
        {
            if (MatchWall(poly[i], poly[(i + 1) % n], walls, out XYZ p, out XYZ d))
            {
                linePt[i] = p;
                lineDir[i] = d;
            }
        }

        var result = new List<XYZ>(n);
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            bool mp = lineDir[prev] != null, mc = lineDir[i] != null;
            XYZ v = poly[i];

            if (mp && mc)
            {
                if (AngleBetween(lineDir[prev], lineDir[i]) > CornerParallel)
                {
                    var x = IntersectLines(linePt[prev], lineDir[prev], linePt[i], lineDir[i]);
                    v = (x != null && x.DistanceTo(poly[i]) <= MaxCornerReach)
                        ? x
                        : ProjectOnLine(poly[i], linePt[i], lineDir[i]);
                }
                else
                {
                    // Two near-parallel walls (a straight run DP split in two) — one line, just project.
                    v = ProjectOnLine(poly[i], linePt[i], lineDir[i]);
                }
            }
            else if (mc) v = ProjectOnLine(poly[i], linePt[i], lineDir[i]);
            else if (mp) v = ProjectOnLine(poly[i], linePt[prev], lineDir[prev]);

            result.Add(v);
        }
        return result;
    }

    // Nearest wall (by perpendicular offset) that is near-parallel to edge a→b and shares a run with it.
    private static bool MatchWall(XYZ a, XYZ b, List<CadWallSegment> walls, out XYZ linePt, out XYZ lineDir)
    {
        linePt = null;
        lineDir = null;

        var ed = b - a;
        double elen = ed.GetLength();
        if (elen < 1e-6) return false;
        var edir = ed / elen;
        var mid = (a + b) * 0.5;

        double best = MaxAlignDist;
        foreach (var seg in walls)
        {
            var sd = seg.EndPoint - seg.StartPoint;
            double slen = sd.GetLength();
            if (slen < 1e-6) continue;
            var sdir = sd / slen;

            if (AngleBetween(edir, sdir) > MaxAlignAngle) continue;

            double dist = PerpDistance(mid, seg.StartPoint, sdir);
            if (dist >= best) continue;
            if (!Overlaps(a, b, seg.StartPoint, sdir, slen)) continue;

            best = dist;
            linePt = seg.StartPoint;
            lineDir = sdir;
        }
        return lineDir != null;
    }

    // True if edge a→b shares at least MinAlignOverlap of its length with segment [origin, origin+slen*dir].
    private static bool Overlaps(XYZ a, XYZ b, XYZ origin, XYZ dir, double slen)
    {
        double ta = (a - origin).X * dir.X + (a - origin).Y * dir.Y;
        double tb = (b - origin).X * dir.X + (b - origin).Y * dir.Y;
        if (ta > tb) (ta, tb) = (tb, ta);
        double lo = Math.Max(ta, 0.0);
        double hi = Math.Min(tb, slen);
        return hi - lo > MinAlignOverlap;
    }

    // Unsigned angle in [0, π/2] between two unit directions (direction-agnostic).
    private static double AngleBetween(XYZ u, XYZ v)
    {
        double d = Math.Min(1.0, Math.Abs(u.X * v.X + u.Y * v.Y));
        return Math.Acos(d);
    }

    private static double PerpDistance(XYZ p, XYZ origin, XYZ dir)
    {
        double along = (p - origin).X * dir.X + (p - origin).Y * dir.Y;
        var proj = new XYZ(origin.X + along * dir.X, origin.Y + along * dir.Y, 0);
        return p.DistanceTo(proj);
    }

    private static XYZ ProjectOnLine(XYZ p, XYZ origin, XYZ dir)
    {
        double along = (p - origin).X * dir.X + (p - origin).Y * dir.Y;
        return new XYZ(origin.X + along * dir.X, origin.Y + along * dir.Y, 0);
    }

    // Drop consecutive near-duplicate vertices (< 0.5") then collinear ones, cyclically. Keeps
    // FilledRegion.Create from choking on zero-length or straight-through edges.
    private static List<XYZ> Cleanup(List<XYZ> poly)
    {
        const double eps = 0.5 / 12.0; // 0.5"

        var dedup = new List<XYZ>(poly.Count);
        foreach (var p in poly)
            if (dedup.Count == 0 || dedup[^1].DistanceTo(p) > eps) dedup.Add(p);
        if (dedup.Count > 1 && dedup[0].DistanceTo(dedup[^1]) <= eps) dedup.RemoveAt(dedup.Count - 1);
        if (dedup.Count < 3) return dedup;

        var result = new List<XYZ>(dedup.Count);
        int n = dedup.Count;
        for (int i = 0; i < n; i++)
        {
            var prev = dedup[(i - 1 + n) % n];
            var cur = dedup[i];
            var next = dedup[(i + 1) % n];
            if (PointToSegmentDistance(cur, prev, next) > eps) result.Add(cur);
        }
        return result.Count >= 3 ? result : dedup;
    }

    // A self-touch closer than this is a zero-width neck (AlignToWalls collapsed a narrow throat onto
    // itself). It never *properly* crosses, so SegmentsCross misses it — but Revit rejects the loop
    // ("input curve loops cannot compose a valid boundary"). One raster pixel = 1", so two non-adjacent
    // boundary features within 1" can only be a pinch, never two genuine corners of the same room.
    private const double SelfTouchTol = 1.0 / 12.0; // 1"

    // null if the polygon is simple; otherwise a short diagnostic naming the self-intersection kind and its
    // model-coordinate — two non-adjacent edges either *properly cross* OR *near-touch* (a vertex-pinch /
    // zero-width neck within SelfTouchTol). Cheap gate (O(n²), n is tiny) to reject a polygon that alignment
    // folded onto itself; the caller skips + names the territory.
    private static string SelfIntersectionReason(List<XYZ> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a1 = poly[i];
            var a2 = poly[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // adjacent across the wrap
                var b1 = poly[j];
                var b2 = poly[(j + 1) % n];
                if (SegmentsCross(a1, a2, b1, b2)) return $"self-cross near {Fmt(b1)}";
                // Vertex-pinch: a non-adjacent vertex sitting on (or a hair off) another edge. Checking the
                // start vertex of each edge covers every vertex over the full i/j sweep.
                if (PointToSegmentDistance(a1, b1, b2) < SelfTouchTol) return $"near-touch at {Fmt(a1)}";
                if (PointToSegmentDistance(b1, a1, a2) < SelfTouchTol) return $"near-touch at {Fmt(b1)}";
            }
        }
        return null;
    }

    private static string Fmt(XYZ p) => $"({p.X:F1}, {p.Y:F1})";

    // Proper crossing only (shared endpoints / collinear touches ignored — Cleanup handles those).
    private static bool SegmentsCross(XYZ p1, XYZ p2, XYZ p3, XYZ p4)
    {
        double d1 = Cross(p3, p4, p1);
        double d2 = Cross(p3, p4, p2);
        double d3 = Cross(p1, p2, p3);
        double d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(XYZ a, XYZ b, XYZ c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    // Intersection of infinite lines (p1 + t·d1) and (p2 + s·d2), or null if near-parallel.
    private static XYZ IntersectLines(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
    {
        double cross = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(cross) < 1e-9) return null;
        double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / cross;
        return new XYZ(p1.X + t * d1.X, p1.Y + t * d1.Y, 0);
    }

    private static double PointToSegmentDistance(XYZ p, XYZ a, XYZ b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return p.DistanceTo(a);

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Max(0, Math.Min(1, t));
        var proj = new XYZ(a.X + t * dx, a.Y + t * dy, 0);
        return p.DistanceTo(proj);
    }
}
