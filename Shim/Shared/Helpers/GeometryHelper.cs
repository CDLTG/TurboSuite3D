using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Bubble.Constants;
using TurboSuite.Shared.Services;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Consolidated geometry helper methods used across multiple commands.
/// </summary>
public static class GeometryHelper
{
    private const double NormalEpsilon = 0.001;

    /// <summary>
    /// Determines if a hosted fixture is mounted on a vertical face (e.g., wall) — the strategy
    /// selector for TurboTag/TurboBubble wall placement.
    ///
    /// Derived from the fixture's OWN transform, not by re-resolving the host-face reference:
    /// <c>host != null</c> AND <see cref="GetWallFaceNormalFromTransform"/>'s primary candidate
    /// (Hand × Facing) has a usable horizontal component. This is the same reasoning as that
    /// helper — see its remarks for why the reference path was abandoned.
    ///
    /// Why this replaced the old host-face-reference Z-test (a retired GetHostFaceNormal that resolved
    /// the HostFace to a PlanarFace and checked its normal): a fixture face-hosted to a
    /// *family* in the link (casework, doors — geometry nested in a GeometryInstance) resolves
    /// LINEAR/NONE, so the reference returned no PlanarFace and the fixture was mis-classified as
    /// non-vertical, losing wall placement entirely. Validated in-model via TurboSpike across a broad
    /// mixed selection (101 fixtures): this predicate DROPS zero fixtures the reference path resolved
    /// as vertical, and ADDS exactly the casework/door-hosted wall fixtures it used to miss.
    ///
    /// The <c>host != null</c> clause is load-bearing: it keeps a genuinely unhosted (host == null)
    /// fixture — e.g. an "&lt;not associated&gt;" switch, a modeling error — classified non-vertical,
    /// diverging it from a real-but-casework-hosted fixture that this now correctly rescues. A truly
    /// unhosted 2D drafting family is unaffected (it was, and stays, non-vertical here — TurboTag
    /// routes it via IsVerticalFamily/IsWallSconce instead).
    /// </summary>
    public static bool IsOnVerticalFace(FamilyInstance fixture)
    {
        if (fixture.Host == null)
            return false;

        var transformNormal = fixture.HandOrientation.CrossProduct(fixture.FacingOrientation);
        var horizontal = new XYZ(transformNormal.X, transformNormal.Y, 0);
        return horizontal.GetLength() > NormalEpsilon;
    }

    /// <summary>
    /// Determines if a fixture is a line-based family (e.g., linear light fixtures).
    /// Line-based families have a LocationCurve instead of a LocationPoint.
    /// </summary>
    public static bool IsLineBasedFixture(FamilyInstance fixture)
    {
        return fixture.Location is LocationCurve;
    }

    /// <summary>
    /// Outward horizontal wall normal for a wall-mounted fixture, derived from the fixture's
    /// OWN transform rather than by re-resolving the host-face reference across the linked model.
    ///
    /// Why: resolving the HostFace reference to a PlanarFace (the retired reference path) only
    /// succeeds when the host is a wall (a top-level BREP solid face, REFERENCE_TYPE_SURFACE). A
    /// fixture face-hosted to a *family*
    /// in the link (casework, doors — geometry nested inside a GeometryInstance) resolves
    /// LINEAR/NONE, so the reference path returned null and the offset collapsed to zero (tag on
    /// the wall) or flipped to the opposite face — and it was nondeterministic across link regens.
    /// The transform (HandOrientation × FacingOrientation) is baked at placement and stable.
    ///
    /// Priority (validated in-model via TurboSpike — matched the correctly resolved host-face normal
    /// on all 41 hosted keypads and all 12 hosted sconces of commit 471ef22, plus a later broad
    /// parity sweep, and yielded the correct value even where the reference failed to resolve):
    /// 1. Hand × Facing horizontalized, if it has a usable horizontal component. Non-degenerate
    ///    for wall fixtures whose Facing is vertical (0,0,1) — the AL_..._(Hosted) convention.
    /// 2. Else FacingOrientation horizontalized — the genuine-2D case, where in-plane facing makes
    ///    Hand × Facing vertical/degenerate. (Matches the old helper's fallback.)
    /// 3. Else XYZ.BasisY — last-ditch, matches the old helper.
    ///
    /// MIRROR CORRECTION: a mirrored face-based instance has a left-handed basis, so Hand × Facing
    /// points INTO the wall, not out. Negate it when fixture.Mirrored. Verified via TurboSpike: 3
    /// mirrored wall fixtures (keypad/receptacle/sconce, Facing = (0,0,1)) flipped 180° without this
    /// (dot = -1 vs the resolved host-face normal) and agreed exactly with it once negated. It is a
    /// no-op for degenerate (facing-fallback) fixtures — negating a zero-length horizontal cross
    /// changes nothing, and the FacingOrientation fallback is not mirror-sensitive — so mirrored
    /// ceiling/recessed fixtures are unaffected.
    ///
    /// Gate on "is candidate 1 horizontally usable?", not on HostFace != null: this also covers an
    /// orphaned face-based fixture whose host is null but whose transform still encodes the wall
    /// frame. Never returns zero-length.
    /// </summary>
    public static XYZ GetWallFaceNormalFromTransform(FamilyInstance fixture)
    {
        var transformNormal = fixture.HandOrientation.CrossProduct(fixture.FacingOrientation);
        if (fixture.Mirrored)
            transformNormal = transformNormal.Negate();

        var horizontal = new XYZ(transformNormal.X, transformNormal.Y, 0);
        if (horizontal.GetLength() > NormalEpsilon)
            return horizontal.Normalize();

        // Genuine 2D case: facing is in-plane, so Hand × Facing is vertical/degenerate.
        var facingOrientation = fixture.FacingOrientation;
        var facing = new XYZ(facingOrientation.X, facingOrientation.Y, 0);
        return facing.GetLength() > NormalEpsilon ? facing.Normalize() : XYZ.BasisY;
    }

    /// <summary>
    /// Determines if a fixture is a wall sconce family (3D hosted or 2D unhosted).
    /// </summary>
    public static bool IsWallSconce(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.Family?.Name ?? "";
        var settings = FamilyNameSettingsCache.Get(fixture.Document);
        return settings.WallSconceFamilies.Contains(familyName);
    }

    /// <summary>
    /// Determines if a fixture is a vertical/wall-mounted family (2D unhosted families
    /// that should receive face-based tag placement).
    /// </summary>
    public static bool IsVerticalFamily(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.Family?.Name ?? "";
        var settings = FamilyNameSettingsCache.Get(fixture.Document);
        return settings.VerticalFamilies.Contains(familyName);
    }

    /// <summary>
    /// Determines if a fixture is a receptacle family (3D hosted or 2D unhosted).
    /// </summary>
    public static bool IsReceptacle(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.Family?.Name ?? "";
        var settings = FamilyNameSettingsCache.Get(fixture.Document);
        return settings.ReceptacleFamilies.Contains(familyName);
    }

    public static bool IsCeilingFan(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.FamilyName ?? "";
        return BubbleConstants.CeilingFanFamilies.Contains(familyName);
    }

    public static bool IsSwitch(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.Family?.Name ?? "";
        var settings = FamilyNameSettingsCache.Get(fixture.Document);
        return settings.SwitchFamilies.Contains(familyName);
    }

    /// <summary>
    /// Gets the location point of a fixture, handling both LocationPoint and LocationCurve.
    /// For line-based fixtures (LocationCurve), returns the curve midpoint.
    /// Returns null if location cannot be determined.
    /// </summary>
    public static XYZ? GetFixtureLocation(FamilyInstance fixture)
    {
        if (fixture.Location is LocationPoint lp)
            return lp.Point;
        if (fixture.Location is LocationCurve lc && lc.Curve != null)
            return lc.Curve.Evaluate(0.5, true);
        return null;
    }

    /// <summary>
    /// Returns the 2D rotation angle (radians) of a transform's BasisX axis.
    /// Why: per CLAUDE.md, only BasisX should be used for direction math —
    /// BasisY/BasisZ are inverted for ceiling-hosted fixtures and cause flips.
    /// </summary>
    public static double GetTransformAngle(Transform transform)
        => Math.Atan2(transform.BasisX.Y, transform.BasisX.X);

    /// <summary>
    /// Gets the rotation of a fixture from its Location property.
    /// For LocationPoint, returns the Rotation property.
    /// For LocationCurve, returns the angle of the curve direction.
    /// Returns 0.0 if rotation cannot be determined.
    /// </summary>
    public static double GetFixtureLocationRotation(FamilyInstance fixture)
    {
        if (fixture.Location is LocationPoint lp)
            return lp.Rotation;
        if (fixture.Location is LocationCurve lc && lc.Curve != null)
        {
            var dir = (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize();
            return Math.Atan2(dir.Y, dir.X);
        }
        return 0.0;
    }

    /// <summary>
    /// Returns (length, width) of the fixture's 2D plan symbol in fixture-local coordinates.
    /// For 3D families, isolates the nested Generic Annotation geometry ("Symbol" family).
    /// For 2D families (no nested annotation), uses the full element bounding box.
    /// Length = local Y extent, Width = local X extent.
    /// </summary>
    public static (double length, double width) GetSymbolExtents(FamilyInstance fixture, View view, double defaultSize)
    {
        double globalDx, globalDy;

        var annotationBounds = GetAnnotationGeometryBounds(fixture, view);
        if (annotationBounds.HasValue)
        {
            globalDx = annotationBounds.Value.maxX - annotationBounds.Value.minX;
            globalDy = annotationBounds.Value.maxY - annotationBounds.Value.minY;
        }
        else
        {
            BoundingBoxXYZ? bbox = fixture.get_BoundingBox(view);
            if (bbox == null)
                return (defaultSize, defaultSize);

            globalDx = bbox.Max.X - bbox.Min.X;
            globalDy = bbox.Max.Y - bbox.Min.Y;
        }

        return GlobalToLocalExtents(fixture, globalDx, globalDy, defaultSize);
    }

    /// <summary>
    /// Returns the distance from the fixture origin to the far edge of its annotation symbol
    /// in the given global direction. Projects actual annotation curve points (not AABB corners)
    /// onto the direction vector for accurate results at any rotation.
    /// Falls back to defaultSize if annotation geometry cannot be determined.
    /// </summary>
    public static double GetSymbolExtentInDirection(FamilyInstance fixture, View view, XYZ direction, double defaultSize)
    {
        XYZ? origin = GetFixtureLocation(fixture);
        if (origin == null)
            return defaultSize;

        double maxProjection = GetAnnotationMaxProjection(fixture, view, origin, direction);

        if (maxProjection <= NormalEpsilon)
        {
            // Fallback to bounding box corners
            BoundingBoxXYZ? bbox = fixture.get_BoundingBox(view);
            if (bbox == null)
                return defaultSize;

            foreach (var corner in new[]
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, 0),
                new XYZ(bbox.Max.X, bbox.Min.Y, 0),
                new XYZ(bbox.Min.X, bbox.Max.Y, 0),
                new XYZ(bbox.Max.X, bbox.Max.Y, 0)
            })
            {
                double projection = (corner - origin).DotProduct(direction);
                maxProjection = Math.Max(maxProjection, projection);
            }
        }

        return maxProjection > NormalEpsilon ? maxProjection : defaultSize;
    }

    private static double GetAnnotationMaxProjection(FamilyInstance fixture, View view, XYZ origin, XYZ direction)
    {
        using var options = new Options { View = view, IncludeNonVisibleObjects = true };
        GeometryElement? geomElement = fixture.get_Geometry(options);
        if (geomElement == null) return 0;

        Document doc = fixture.Document;
        var annotationCatId = new ElementId(BuiltInCategory.OST_GenericAnnotation);

        double maxProjection = 0;

        foreach (GeometryObject obj in geomElement)
        {
            if (obj is not Curve curve) continue;
            if (!IsAnnotationCurve(doc, obj, annotationCatId)) continue;

            foreach (XYZ pt in curve.Tessellate())
            {
                double projection = (pt - origin).DotProduct(direction);
                maxProjection = Math.Max(maxProjection, projection);
            }
        }

        return maxProjection;
    }

    private static (double length, double width) GlobalToLocalExtents(FamilyInstance fixture, double globalDx, double globalDy, double defaultSize)
    {
        double angle = GetTransformAngle(fixture.GetTransform());

        double absC = Math.Abs(Math.Cos(angle));
        double absS = Math.Abs(Math.Sin(angle));
        double det = absC * absC - absS * absS;

        double localWidth, localLength;

        if (Math.Abs(det) < 0.1)
        {
            localWidth = Math.Max(globalDx, globalDy);
            localLength = localWidth;
        }
        else
        {
            localWidth  = (globalDx * absC - globalDy * absS) / det;
            localLength = (globalDy * absC - globalDx * absS) / det;
            localWidth  = Math.Max(localWidth, 0);
            localLength = Math.Max(localLength, 0);
        }

        if (localLength < NormalEpsilon || localWidth < NormalEpsilon)
            return (defaultSize, defaultSize);

        return (localLength, localWidth);
    }

    private static (double minX, double minY, double maxX, double maxY)? GetAnnotationGeometryBounds(FamilyInstance fixture, View view)
    {
        // IncludeNonVisibleObjects exposes the nested annotation symbol geometry
        // as top-level Curve objects, separate from the GeometryInstance (3D geometry).
        // Filter to Generic Annotations category to exclude Light Source wireframe edges.
        using var options = new Options { View = view, IncludeNonVisibleObjects = true };
        GeometryElement? geomElement = fixture.get_Geometry(options);
        if (geomElement == null) return null;

        Document doc = fixture.Document;
        var annotationCatId = new ElementId(BuiltInCategory.OST_GenericAnnotation);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool found = false;

        foreach (GeometryObject obj in geomElement)
        {
            if (obj is not Curve curve) continue;
            if (!IsAnnotationCurve(doc, obj, annotationCatId)) continue;

            foreach (XYZ pt in curve.Tessellate())
            {
                minX = Math.Min(minX, pt.X);
                minY = Math.Min(minY, pt.Y);
                maxX = Math.Max(maxX, pt.X);
                maxY = Math.Max(maxY, pt.Y);
                found = true;
            }
        }

        return found ? (minX, minY, maxX, maxY) : null;
    }

    private static bool IsAnnotationCurve(Document doc, GeometryObject obj, ElementId annotationCatId)
    {
        if (obj.GraphicsStyleId == ElementId.InvalidElementId) return false;
        if (doc.GetElement(obj.GraphicsStyleId) is not GraphicsStyle style) return false;
        return style.GraphicsStyleCategory?.Id == annotationCatId;
    }

    /// <summary>
    /// Gets the first electrical connector from a fixture.
    /// </summary>
    public static Connector? GetElectricalConnector(FamilyInstance fixture)
    {
        var connectors = fixture.MEPModel?.ConnectorManager?.Connectors;
        if (connectors == null) return null;

        foreach (Connector conn in connectors)
        {
            if (conn.Domain == Domain.DomainElectrical)
                return conn;
        }
        return null;
    }

    /// <summary>
    /// Gets the first electrical connector from a fixture, optionally filtering to End type only.
    /// </summary>
    public static Connector? GetElectricalConnector(FamilyInstance fixture, bool endTypeOnly)
    {
        if (!endTypeOnly)
            return GetElectricalConnector(fixture);

        var connectors = fixture.MEPModel?.ConnectorManager?.Connectors;
        if (connectors == null) return null;

        foreach (Connector conn in connectors)
        {
            if (conn.Domain == Domain.DomainElectrical &&
                conn.ConnectorType == ConnectorType.End)
                return conn;
        }
        return null;
    }
}
