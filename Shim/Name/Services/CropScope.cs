#nullable disable
using System;
using Autodesk.Revit.DB;

namespace TurboSuite.Name.Services;

/// <summary>
/// The active view's crop box as a model-space AABB, plus the point/segment tests that decide whether a
/// piece of geometry belongs to "this floor".
///
/// <b>Why this exists.</b> On a stacked multi-floor DWG the crop box is the ONLY thing that says which floor
/// the user is working on — every floor's regions live in the same view and the same document. Generation has
/// always honoured it (<see cref="RegionWatershedService"/> clips walls/doors/seeds before partitioning), but
/// a view-scoped <c>FilteredElementCollector</c> does not: it returns everything the view owns regardless of
/// the crop, so the clear planner saw the whole stack. Cropping to level 2 and pressing Auto-generate offered
/// to clear level 1's regions, and accepting deleted them without regenerating them — the watershed only ever
/// returns crop-clipped territories.
///
/// <b>Rotated crops.</b> This is the AABB of the eight transformed corners, so a rotated crop scopes to a box
/// slightly larger than what the user sees. That has always been generation's behaviour; matching it here is
/// deliberate, because the two sides must agree on the same box or the asymmetry comes straight back.
///
/// <b>Inactive crop.</b> <see cref="IsActive"/> false makes every test return true, so an uncropped view
/// behaves exactly as it did before this type existed.
/// </summary>
public sealed class CropScope
{
    private readonly double _minX, _minY, _maxX, _maxY;

    public bool IsActive { get; }

    private CropScope(bool isActive, double minX, double minY, double maxX, double maxY)
    {
        IsActive = isActive;
        _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY;
    }

    /// <summary>Model-space AABB of the view's crop box (8 transformed corners → XY min/max).</summary>
    public static CropScope For(View view)
    {
        if (view == null || !view.CropBoxActive)
            return new CropScope(false, 0, 0, 0, 0);

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
        return new CropScope(true, minX, minY, maxX, maxY);
    }

    public bool Contains(XYZ p) =>
        !IsActive || (p != null && p.X >= _minX && p.X <= _maxX && p.Y >= _minY && p.Y <= _maxY);

    /// <summary>Keep a segment if its bbox overlaps the crop bbox.</summary>
    public bool OverlapsSegment(XYZ a, XYZ b)
    {
        if (!IsActive) return true;
        double sMinX = Math.Min(a.X, b.X), sMaxX = Math.Max(a.X, b.X);
        double sMinY = Math.Min(a.Y, b.Y), sMaxY = Math.Max(a.Y, b.Y);
        return sMinX <= _maxX && sMaxX >= _minX && sMinY <= _maxY && sMaxY >= _minY;
    }

    /// <summary>
    /// Whether an element belongs to this floor, by its bounding-box CENTRE.
    /// </summary>
    /// <remarks>
    /// Centre, not bbox-overlap, and that choice is load-bearing. Generation keeps a territory when its SEED
    /// point lands in the crop (<see cref="RegionWatershedService"/>), so the clear side has to be a point test
    /// too — an overlap test would sweep in a region that merely grazes the crop edge, whose seed sits outside
    /// and which therefore never gets regenerated. That is the original bug in miniature, just at the boundary
    /// instead of at the floor.
    ///
    /// The centre can fall outside an L-shaped region's own boundary; irrelevant here, since the only question
    /// is which crop it falls in.
    ///
    /// Returns true when the element has no bounding box in this view. Such a region is degenerate debris that
    /// cannot be attributed to a floor at all, and <see cref="RegionClearService.CollectClearableRegions"/>
    /// exists to make sure nothing like that escapes the clear.
    /// </remarks>
    public bool ContainsElement(Element element, View view)
    {
        if (!IsActive) return true;
        var bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (bb == null) return true;
        return Contains((bb.Min + bb.Max) / 2.0);
    }
}
