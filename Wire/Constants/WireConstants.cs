namespace TurboSuite.Wire.Constants;

internal static class WireConstants
{
    public const double ArcAngleDegrees = 24.0;
    public const double MinDistanceTolerance = 1e-6;
    public const string WallSconceFamilyName = "AL_Decorative_Wall Sconce (Hosted)";

    // Wall sconce spline offsets (in feet) - baseline for 4' apart
    public const double BaselineDistance = 4.0;
    public const double SplineOffsetAlongWall = 6.0 / 12.0;
    public const double SplineOffsetNearWall = 1.5 / 12.0;
    public const double SplineOffsetMidFromWall = 6.5 / 12.0;
    public const double SplineConnectorOffset = 2.5 / 12.0;

    // Receptacle family names (2D unhosted and 3D hosted)
    public const string ReceptacleFamilyName2D = "Receptacle";
    public const string ReceptacleFamilyName3D = "AL_Electrical Fixture_Receptacle (Hosted)";

    // Receptacle spline offsets (in feet) - shifted 0.5" away from wall vs sconce
    public const double ReceptacleSplineConnectorOffset = 3.0 / 12.0;
    public const double ReceptacleSplineOffsetNearWall = 2.0 / 12.0;
    public const double ReceptacleSplineOffsetMidFromWall = 7.0 / 12.0;
}
