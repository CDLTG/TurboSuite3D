#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Rasterizes wall segments onto a bitmap and provides per-click flood fill + vectorization.
/// Instance-based to hold bitmap state across the pick loop.
/// </summary>
public class RasterRegionService
{
    private const double TargetPixelsPerFoot = 12.0; // 1 inch per pixel (before cap)
    private const int WallValue = -1;
    private const int UnfilledValue = 0;
    private const double SimplifyTolerance = 1.5 / 12.0; // 1.5 inches in feet
    private const double SnapTolerance = 2.0 / 12.0;     // 2 inches in feet

    private readonly int _width;
    private readonly int _height;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _pixelsPerFoot; // actual resolution (may be reduced by cap)
    private readonly int[] _bitmap;
    private readonly List<CadWallSegment> _wallSegments;
    private int _nextFillId = 1;
    private int _lastFillId;
    private HashSet<int> _lastFillPixels;

    /// <summary>Diagnostic info from the last FillFromPoint call.</summary>
    public string LastStatus { get; private set; }

    /// <summary>Bitmap dimensions for diagnostics.</summary>
    public string DiagnosticInfo => $"Bitmap: {_width}x{_height} ({_pixelsPerFoot:F1}px/ft), Bounds: ({_minX:F1},{_minY:F1})-({_minX + _width / _pixelsPerFoot:F1},{_minY + _height / _pixelsPerFoot:F1})";

    public RasterRegionService(List<CadWallSegment> wallSegments)
    {
        _wallSegments = wallSegments;

        // Compute bounding box with 2 ft padding
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var seg in wallSegments)
        {
            minX = Math.Min(minX, Math.Min(seg.StartPoint.X, seg.EndPoint.X));
            minY = Math.Min(minY, Math.Min(seg.StartPoint.Y, seg.EndPoint.Y));
            maxX = Math.Max(maxX, Math.Max(seg.StartPoint.X, seg.EndPoint.X));
            maxY = Math.Max(maxY, Math.Max(seg.StartPoint.Y, seg.EndPoint.Y));
        }

        _minX = minX - 2.0;
        _minY = minY - 2.0;
        double spanX = maxX - minX + 4.0;
        double spanY = maxY - minY + 4.0;
        _pixelsPerFoot = TargetPixelsPerFoot;

        _width = (int)(spanX * _pixelsPerFoot) + 1;
        _height = (int)(spanY * _pixelsPerFoot) + 1;

        // Cap bitmap at ~200 MB (int = 4 bytes). Multi-level DWGs commonly place
        // floor plans hundreds of feet apart, so the cap must be generous.
        if ((long)_width * _height > 50_000_000)
        {
            double scale = Math.Sqrt(50_000_000.0 / ((long)_width * _height));
            _pixelsPerFoot = TargetPixelsPerFoot * scale;
            _width = (int)(spanX * _pixelsPerFoot) + 1;
            _height = (int)(spanY * _pixelsPerFoot) + 1;
        }

        _bitmap = new int[_width * _height];

        // Rasterize all wall segments
        foreach (var seg in wallSegments)
            RasterizeLine(seg.StartPoint, seg.EndPoint);

        // Dilate walls — scale rounds with resolution to maintain ~6 inch gap closure.
        // Formula: ceil(0.25 * px/ft) rounds, so each side grows by ~3 inches.
        int dilationRounds = Math.Max(2, (int)Math.Ceiling(0.25 * _pixelsPerFoot));
        dilationRounds = Math.Min(dilationRounds, 4);
        for (int i = 0; i < dilationRounds; i++)
            Dilate();
    }

    /// <summary>
    /// Marks pixels covered by an existing FilledRegion as claimed.
    /// Call during Resume to respect manual edits.
    /// </summary>
    public void ClaimExistingRegion(List<List<XYZ>> boundaryLoops)
    {
        if (boundaryLoops == null || boundaryLoops.Count == 0) return;

        int claimId = _nextFillId++;
        var outerLoop = boundaryLoops[0];
        if (outerLoop.Count < 3) return;

        // Scan-fill the polygon
        int pyMin = int.MaxValue, pyMax = int.MinValue;
        foreach (var pt in outerLoop)
        {
            int py = ToPixelY(pt.Y);
            pyMin = Math.Min(pyMin, py);
            pyMax = Math.Max(pyMax, py);
        }

        pyMin = Math.Max(0, pyMin);
        pyMax = Math.Min(_height - 1, pyMax);

        for (int py = pyMin; py <= pyMax; py++)
        {
            var intersections = new List<int>();
            for (int i = 0; i < outerLoop.Count; i++)
            {
                int j = (i + 1) % outerLoop.Count;
                int y1 = ToPixelY(outerLoop[i].Y);
                int y2 = ToPixelY(outerLoop[j].Y);
                if (y1 == y2) continue;
                if (py < Math.Min(y1, y2) || py >= Math.Max(y1, y2)) continue;

                int x1 = ToPixelX(outerLoop[i].X);
                int x2 = ToPixelX(outerLoop[j].X);
                int ix = x1 + (py - y1) * (x2 - x1) / (y2 - y1);
                intersections.Add(ix);
            }

            intersections.Sort();
            for (int k = 0; k + 1 < intersections.Count; k += 2)
            {
                int xStart = Math.Max(0, intersections[k]);
                int xEnd = Math.Min(_width - 1, intersections[k + 1]);
                for (int px = xStart; px <= xEnd; px++)
                {
                    int idx = py * _width + px;
                    if (_bitmap[idx] == UnfilledValue)
                        _bitmap[idx] = claimId;
                }
            }
        }
    }

    /// <summary>
    /// Flood-fills from a click point. Returns boundary polygon or null if the point
    /// hits a wall or already-claimed area. Marks filled pixels as claimed.
    /// </summary>
    public List<XYZ> FillFromPoint(XYZ clickPoint)
    {
        int px = ToPixelX(clickPoint.X);
        int py = ToPixelY(clickPoint.Y);

        if (px < 0 || px >= _width || py < 0 || py >= _height)
        {
            LastStatus = $"Out of bounds: click ({clickPoint.X:F1},{clickPoint.Y:F1}) -> pixel ({px},{py}), bitmap {_width}x{_height}";
            return null;
        }

        int idx = py * _width + px;
        if (_bitmap[idx] != UnfilledValue)
        {
            LastStatus = _bitmap[idx] == WallValue
                ? $"Hit wall pixel at ({px},{py})"
                : $"Hit claimed pixel (id={_bitmap[idx]}) at ({px},{py})";
            return null;
        }

        int fillId = _nextFillId++;
        var filledPixels = FloodFill(px, py, fillId);

        if (filledPixels.Count < 10)
        {
            foreach (int fi in filledPixels)
                _bitmap[fi] = UnfilledValue;
            LastStatus = $"Fill too small ({filledPixels.Count} pixels) at ({px},{py})";
            return null;
        }

        // Reject fills that are unreasonably large (leaked through gaps)
        double fillAreaSqFt = filledPixels.Count / (_pixelsPerFoot * _pixelsPerFoot);
        if (fillAreaSqFt > 5000)
        {
            foreach (int fi in filledPixels)
                _bitmap[fi] = UnfilledValue;
            LastStatus = $"Fill too large ({fillAreaSqFt:F0} sq ft) — walls likely have gaps. Try fewer wall layers or check CAD data.";
            return null;
        }

        _lastFillId = fillId;
        _lastFillPixels = filledPixels;

        // Trace contour
        var contour = TraceContour(filledPixels, fillId);
        if (contour.Count < 3) return null;

        // Simplify (Douglas-Peucker)
        var simplified = DouglasPeucker(contour, SimplifyTolerance);
        if (simplified.Count < 3) return null;

        // Snap to wall segments
        var snapped = SnapToWalls(simplified);

        return snapped;
    }

    /// <summary>
    /// Undoes the last fill — un-claims those pixels.
    /// </summary>
    public void UndoLastFill()
    {
        if (_lastFillPixels == null) return;
        foreach (int idx in _lastFillPixels)
        {
            if (_bitmap[idx] == _lastFillId)
                _bitmap[idx] = UnfilledValue;
        }
        _lastFillPixels = null;
    }

    /// <summary>
    /// Exports the bitmap to a PNG file for visual debugging.
    /// Black = wall, white = unfilled, blue = claimed/filled.
    /// </summary>
    public void ExportDebugBitmap(string filePath)
    {
        var palette = new BitmapPalette(new List<System.Windows.Media.Color>
        {
            Colors.White,                        // 0 = unfilled
            Colors.Black,                        // 1 = wall
            System.Windows.Media.Color.FromRgb(100, 140, 255), // 2 = claimed
        });
        var wb = new WriteableBitmap(_width, _height, 96, 96,
            PixelFormats.Indexed8, palette);
        var pixels = new byte[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            // Flip Y so bitmap matches Revit orientation (Y-up)
            int srcRow = (_height - 1 - y) * _width;
            int dstRow = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int val = _bitmap[srcRow + x];
                if (val == WallValue) pixels[dstRow + x] = 1;
                else if (val != UnfilledValue) pixels[dstRow + x] = 2;
                // else 0 (unfilled/white)
            }
        }
        wb.WritePixels(new Int32Rect(0, 0, _width, _height), pixels, _width, 0);
        using var stream = File.Create(filePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        encoder.Save(stream);
    }

    private void RasterizeLine(XYZ start, XYZ end)
    {
        int x0 = ToPixelX(start.X);
        int y0 = ToPixelY(start.Y);
        int x1 = ToPixelX(end.X);
        int y1 = ToPixelY(end.Y);

        // Bresenham's line algorithm
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetWall(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private void Dilate()
    {
        // Find all wall pixels, then expand by 1 in 8-connected directions
        var wallIndices = new List<int>();
        for (int i = 0; i < _bitmap.Length; i++)
        {
            if (_bitmap[i] == WallValue)
                wallIndices.Add(i);
        }

        foreach (int idx in wallIndices)
        {
            int y = idx / _width;
            int x = idx % _width;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
                    {
                        int ni = ny * _width + nx;
                        if (_bitmap[ni] == UnfilledValue)
                            _bitmap[ni] = WallValue;
                    }
                }
            }
        }
    }

    private HashSet<int> FloodFill(int startX, int startY, int fillId)
    {
        // Max pixels = 5000 sq ft at current resolution (early termination for leaks)
        int maxPixels = (int)(5000.0 * _pixelsPerFoot * _pixelsPerFoot) + 1;

        var filled = new HashSet<int>();
        var queue = new Queue<int>();
        int startIdx = startY * _width + startX;
        queue.Enqueue(startIdx);
        _bitmap[startIdx] = fillId;
        filled.Add(startIdx);

        while (queue.Count > 0)
        {
            if (filled.Count > maxPixels)
                break; // Early termination — fill is leaking

            int idx = queue.Dequeue();
            int y = idx / _width;
            int x = idx % _width;

            // 4-connected neighbors
            TryEnqueue(x + 1, y, fillId, queue, filled);
            TryEnqueue(x - 1, y, fillId, queue, filled);
            TryEnqueue(x, y + 1, fillId, queue, filled);
            TryEnqueue(x, y - 1, fillId, queue, filled);
        }

        return filled;
    }

    private void TryEnqueue(int x, int y, int fillId, Queue<int> queue, HashSet<int> filled)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) return;
        int idx = y * _width + x;
        if (_bitmap[idx] != UnfilledValue) return;
        _bitmap[idx] = fillId;
        filled.Add(idx);
        queue.Enqueue(idx);
    }

    private List<XYZ> TraceContour(HashSet<int> filledPixels, int fillId)
    {
        // Marching squares on the filled region
        // We trace the boundary between filled (fillId) and non-filled pixels
        var contourPoints = new List<XYZ>();

        // Find starting boundary pixel (topmost, then leftmost)
        int startIdx = -1;
        int startX = int.MaxValue, startY = int.MaxValue;
        foreach (int idx in filledPixels)
        {
            int y = idx / _width;
            int x = idx % _width;
            if (y < startY || (y == startY && x < startX))
            {
                startY = y;
                startX = x;
                startIdx = idx;
            }
        }

        if (startIdx < 0) return contourPoints;

        // Simple boundary tracing: walk the perimeter of the filled region
        // using a direction-based contour follower
        int cx = startX, cy = startY;
        int dir = 0; // 0=right, 1=down, 2=left, 3=up
        int maxSteps = filledPixels.Count * 4;
        bool started = false;

        // Direction deltas: right, down, left, up
        int[] dxArr = { 1, 0, -1, 0 };
        int[] dyArr = { 0, 1, 0, -1 };

        for (int step = 0; step < maxSteps; step++)
        {
            if (started && cx == startX && cy == startY && contourPoints.Count > 2)
                break;

            started = true;
            contourPoints.Add(ToRevit(cx, cy));

            // Turn left, then try forward, right, back
            int leftDir = (dir + 3) % 4;
            if (IsFilled(cx + dxArr[leftDir], cy + dyArr[leftDir], fillId))
            {
                dir = leftDir;
            }
            else if (!IsFilled(cx + dxArr[dir], cy + dyArr[dir], fillId))
            {
                int rightDir = (dir + 1) % 4;
                if (IsFilled(cx + dxArr[rightDir], cy + dyArr[rightDir], fillId))
                {
                    dir = rightDir;
                }
                else
                {
                    dir = (dir + 2) % 4; // Back
                }
            }

            cx += dxArr[dir];
            cy += dyArr[dir];
        }

        return contourPoints;
    }

    private bool IsFilled(int x, int y, int fillId)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) return false;
        return _bitmap[y * _width + x] == fillId;
    }

    private static List<XYZ> DouglasPeucker(List<XYZ> points, double tolerance)
    {
        if (points.Count <= 3) return new List<XYZ>(points);

        // Find the point farthest from the line between first and last
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

    private List<XYZ> SnapToWalls(List<XYZ> polygon)
    {
        var result = new List<XYZ>();
        foreach (var pt in polygon)
        {
            XYZ closest = null;
            double closestDist = SnapTolerance;

            foreach (var seg in _wallSegments)
            {
                var snapped = ClosestPointOnSegment(pt, seg.StartPoint, seg.EndPoint);
                double dist = pt.DistanceTo(snapped);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = snapped;
                }
            }

            result.Add(closest ?? pt);
        }
        return result;
    }

    private static XYZ ClosestPointOnSegment(XYZ p, XYZ a, XYZ b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return a;

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Max(0, Math.Min(1, t));
        return new XYZ(a.X + t * dx, a.Y + t * dy, 0);
    }

    private void SetWall(int x, int y)
    {
        if (x >= 0 && x < _width && y >= 0 && y < _height)
            _bitmap[y * _width + x] = WallValue;
    }

    private int ToPixelX(double x) => (int)((x - _minX) * _pixelsPerFoot);
    private int ToPixelY(double y) => (int)((y - _minY) * _pixelsPerFoot);

    private XYZ ToRevit(int px, int py)
    {
        double x = px / _pixelsPerFoot + _minX;
        double y = py / _pixelsPerFoot + _minY;
        return new XYZ(x, y, 0);
    }
}
