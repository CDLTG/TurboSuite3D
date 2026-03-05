using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Services;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Consolidated geometry helper methods used across multiple commands.
/// </summary>
public static class GeometryHelper
{
    private const double NormalEpsilon = 0.001;

    /// <summary>
    /// Determines if a fixture is mounted on a vertical face (e.g., wall).
    /// Vertical faces have horizontal normals where the Z component is near zero.
    /// </summary>
    public static bool IsOnVerticalFace(FamilyInstance fixture)
    {
        var faceNormal = GetHostFaceNormal(fixture);
        if (faceNormal == null)
            return false;

        return Math.Abs(faceNormal.Z) < NormalEpsilon;
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
    /// Gets the face normal from a fixture's host face.
    /// Supports both local hosts and hosts in linked models.
    /// Returns null if the normal cannot be determined.
    /// </summary>
    public static XYZ? GetHostFaceNormal(FamilyInstance fixture)
    {
        if (fixture.Host == null)
            return null;

        try
        {
            var hostFaceRef = fixture.HostFace;
            if (hostFaceRef == null)
                return null;

            var host = fixture.Host;

            if (host is RevitLinkInstance linkInstance)
            {
                var linkedDoc = linkInstance.GetLinkDocument();
                if (linkedDoc == null)
                    return null;

                var linkedElement = linkedDoc.GetElement(hostFaceRef.LinkedElementId);
                var linkRef = hostFaceRef.CreateReferenceInLink();
                var geomObj = linkedElement?.GetGeometryObjectFromReference(linkRef);

                if (geomObj is PlanarFace planarFace)
                {
                    var linkTransform = linkInstance.GetTotalTransform();
                    return linkTransform.OfVector(planarFace.FaceNormal);
                }
            }
            else
            {
                var geomObj = host.GetGeometryObjectFromReference(hostFaceRef);

                if (geomObj is PlanarFace planarFace)
                {
                    return planarFace.FaceNormal;
                }
            }
        }
        catch
        {
            // Could not get face normal
        }

        return null;
    }

    /// <summary>
    /// Gets the wall face normal for a fixture, normalized to horizontal plane.
    /// Supports both local hosts and hosts in linked models.
    /// Falls back to fixture's facing orientation if wall normal unavailable.
    /// </summary>
    public static XYZ GetWallFaceNormal(FamilyInstance fixture)
    {
        var normal = GetHostFaceNormal(fixture);

        if (normal != null)
        {
            var horizontal = new XYZ(normal.X, normal.Y, 0);
            var length = horizontal.GetLength();
            if (length > NormalEpsilon)
                return horizontal.Normalize();
        }

        // Default to fixture's facing orientation
        var facingOrientation = fixture.FacingOrientation;
        var facing = new XYZ(facingOrientation.X, facingOrientation.Y, 0);
        var facingLength = facing.GetLength();
        return facingLength > NormalEpsilon ? facing.Normalize() : XYZ.BasisY;
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
    /// Determines if a fixture is a receptacle family (3D hosted or 2D unhosted).
    /// </summary>
    public static bool IsReceptacle(FamilyInstance fixture)
    {
        string familyName = fixture.Symbol?.Family?.Name ?? "";
        var settings = FamilyNameSettingsCache.Get(fixture.Document);
        return settings.ReceptacleFamilies.Contains(familyName);
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
