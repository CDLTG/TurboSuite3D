using System;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Shared.Models;

/// <summary>
/// Represents a wall-local coordinate system for a fixture mounted on a wall.
/// X axis = along wall (parallel), Y axis = outward from wall (normal).
/// </summary>
public class WallLocalCoordinateSystem
{
    public XYZ Origin { get; }
    public XYZ WallNormal { get; }
    public XYZ WallParallel { get; }
    public double WallAngle { get; }
    public Transform GlobalToLocal { get; }
    public Transform LocalToGlobal { get; }

    public WallLocalCoordinateSystem(FamilyInstance fixture)
    {
        Origin = GeometryHelper.GetFixtureLocation(fixture)
            ?? throw new InvalidOperationException("Fixture has no valid location.");

        // Transform-derived (mirror-corrected) wall normal, not the host-face reference — so the
        // wall-local frame (Y = out of wall) stays correct for casework/door-hosted fixtures, whose
        // reference falls back to a constant BasisY and would place the spline vertices inside the
        // wall. See GeometryHelper.GetWallFaceNormalFromTransform.
        WallNormal = GeometryHelper.GetWallFaceNormalFromTransform(fixture);
        WallParallel = new XYZ(-WallNormal.Y, WallNormal.X, 0);

        WallAngle = Math.Atan2(WallNormal.Y, WallNormal.X) - Math.PI / 2.0;

        GlobalToLocal = Transform.CreateRotationAtPoint(XYZ.BasisZ, -WallAngle, Origin);
        LocalToGlobal = GlobalToLocal.Inverse;
    }

    public XYZ ToLocal(XYZ globalPoint) => GlobalToLocal.OfPoint(globalPoint);
    public XYZ ToGlobal(XYZ localPoint) => LocalToGlobal.OfPoint(localPoint);
}
