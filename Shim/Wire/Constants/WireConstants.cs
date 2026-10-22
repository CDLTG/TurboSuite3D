namespace TurboSuite.Wire.Constants;

/// <summary>
/// TurboWire's scale-independent constants. Every paper-relative length (spline offsets, switch and
/// nudge offsets, linear end-routing gaps) lives in the view-scaled <see cref="WireGeometry"/> (Core).
/// </summary>
internal static class WireConstants
{
    public const double ArcAngleDegrees = 24.0;
    public const double MinDistanceTolerance = 1e-6;
}
