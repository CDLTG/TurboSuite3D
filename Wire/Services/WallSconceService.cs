using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Models;
using TurboSuite.Wire.Constants;

namespace TurboSuite.Wire.Services;

internal static class WallSconceService
{
    public static bool IsWallSconce(FamilyInstance fixture)
    {
        return GeometryHelper.IsWallSconce(fixture);
    }

    public static bool IsReceptacle(FamilyInstance fixture)
    {
        return GeometryHelper.IsReceptacle(fixture);
    }

    public static bool IsSwitch(FamilyInstance fixture)
    {
        return GeometryHelper.IsSwitch(fixture);
    }

    public static double GetFamilyScaleFactor(FamilyInstance fixture)
    {
        Parameter? scaleParam = fixture.LookupParameter("Scale Factor");
        if (scaleParam != null && scaleParam.HasValue)
        {
            return scaleParam.AsDouble();
        }
        return 1.0;
    }

    public static IList<XYZ> CalculateWallSconceSplinePoints(
        FamilyInstance fixture1, FamilyInstance fixture2,
        XYZ p1, XYZ p2,
        double distance, double familyScaleFactor,
        bool facingSameDirection)
    {
        double scaleFactor = distance / WireConstants.BaselineDistance;

        bool isReceptacle = GeometryHelper.IsReceptacle(fixture1);
        double connectorOffsetConst = isReceptacle ? WireConstants.ReceptacleSplineConnectorOffset : WireConstants.SplineConnectorOffset;
        double nearWallConst = isReceptacle ? WireConstants.ReceptacleSplineOffsetNearWall : WireConstants.SplineOffsetNearWall;
        double midFromWallConst = isReceptacle ? WireConstants.ReceptacleSplineOffsetMidFromWall : WireConstants.SplineOffsetMidFromWall;

        double baselineFromWall = connectorOffsetConst * familyScaleFactor;

        double nearWallDelta = baselineFromWall - (nearWallConst * familyScaleFactor);
        double offsetNearWall = baselineFromWall - (nearWallDelta * scaleFactor);

        double midWallDelta = (midFromWallConst * familyScaleFactor) - baselineFromWall;
        double offsetMidFromWall = baselineFromWall + (midWallDelta * scaleFactor);

        double offsetAlongWall = WireConstants.SplineOffsetAlongWall * familyScaleFactor * scaleFactor;

        var wallCoords1 = new WallLocalCoordinateSystem(fixture1);
        var wallCoords2 = new WallLocalCoordinateSystem(fixture2);

        XYZ p1Local = wallCoords1.ToLocal(p1);
        XYZ p2Local = wallCoords1.ToLocal(p2);

        double direction = Math.Sign(p2Local.X - p1Local.X);
        if (direction == 0) direction = 1;

        double midX = (p1Local.X + p2Local.X) / 2.0;

        XYZ v2Local = new XYZ(
            p1Local.X + direction * offsetAlongWall,
            p1Local.Y + offsetNearWall,
            p1Local.Z);

        XYZ v3, v4Global;

        if (facingSameDirection)
        {
            XYZ v3Local = new XYZ(
                midX,
                p1Local.Y + offsetMidFromWall,
                p1Local.Z);

            XYZ v4Local = new XYZ(
                p2Local.X - direction * offsetAlongWall,
                p2Local.Y + offsetNearWall,
                p2Local.Z);

            v3 = wallCoords1.ToGlobal(v3Local);
            v4Global = wallCoords1.ToGlobal(v4Local);
        }
        else
        {
            XYZ p2Local2 = wallCoords2.ToLocal(p2);
            XYZ p1Local2 = wallCoords2.ToLocal(p1);

            double direction2 = Math.Sign(p1Local2.X - p2Local2.X);
            if (direction2 == 0) direction2 = 1;

            XYZ midGlobal = (p1 + p2) * 0.5;
            v3 = midGlobal + wallCoords1.WallNormal * offsetMidFromWall * 0.5
                          + wallCoords2.WallNormal * offsetMidFromWall * 0.5;
            v3 = new XYZ(v3.X, v3.Y, (p1.Z + p2.Z) / 2.0);

            XYZ v4Local2 = new XYZ(
                p2Local2.X + direction2 * offsetAlongWall,
                p2Local2.Y + offsetNearWall,
                p2Local2.Z);

            v4Global = wallCoords2.ToGlobal(v4Local2);
        }

        return new List<XYZ>
        {
            p1,
            wallCoords1.ToGlobal(v2Local),
            v3,
            v4Global,
            p2
        };
    }
}
