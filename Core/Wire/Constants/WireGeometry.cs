using TurboSuite.Shared.Helpers;

namespace TurboSuite.Wire.Constants;

/// <summary>
/// Every paper-relative length TurboWire routes with, scaled to the placement view. Each member is its
/// 1/4" = 1'-0" reference value × <see cref="PlanScale.K"/>, so a wire keeps the same printed shape at
/// any view scale and is byte-identical at 1/4" (K = 1). Built once per command run from the view being
/// wired (<c>PlanScaleHelper.Of(view)</c>). Public because the shim consumes it across the assembly
/// boundary.
///
/// Values keep their original form (<c>v / 12.0</c>, or bare feet), with K applied last:
/// <c>v * (1.0 / 12.0)</c> rounds differently for several of them, which would move 1/4" output.
/// Lengths only — the arc angle and the coincidence tolerance stay in the shim's <c>WireConstants</c>.
/// </summary>
public sealed class WireGeometry
{
    private readonly double _k;

    public WireGeometry(PlanScale scale)
    {
        Scale = scale;
        _k = scale.K;
    }

    // The scale itself, for geometry that isn't a bundle length: the 2D switch scales its connector's
    // model offset from the family origin by K, since the connector doesn't move with the symbol.
    public PlanScale Scale { get; }

    // Wall sconce spline: offsets drawn at BaselineDistance apart, then stretched by distance / baseline.
    // The baseline scales with the offsets, so a spline at 1/8" is a small copy of the 1/4" one.
    public double BaselineDistance => 4.0 * _k;
    public double SplineOffsetAlongWall => 6.0 / 12.0 * _k;
    public double SplineOffsetNearWall => 1.5 / 12.0 * _k;
    public double SplineOffsetMidFromWall => 6.5 / 12.0 * _k;
    public double SplineConnectorOffset => 2.5 / 12.0 * _k;

    // Receptacle spline: shifted 0.5" further from the wall than the sconce set
    public double ReceptacleSplineConnectorOffset => 3.0 / 12.0 * _k;
    public double ReceptacleSplineOffsetNearWall => 2.0 / 12.0 * _k;
    public double ReceptacleSplineOffsetMidFromWall => 7.0 / 12.0 * _k;

    // 3D wall-hosted switch: wire end pushed out of the wall past the switch symbol
    public double SwitchOffsetFromWall => 9.0 / 12.0 * _k;

    // Spline ends: first step of the two-step vertex nudge that forces Revit's end clipping
    public double WallNudgeInitialOffset => 0.5 / 12.0 * _k;

    // Linear end-to-end routing: end-pairs within EndTieEpsilon of the shortest tie (the bulge decides),
    // and chosen ends closer than MinEndGap collapse back to connector-to-connector
    public double EndTieEpsilon => 0.5 * _k;
    public double MinEndGap => 0.25 * _k;

    // A pair is off-axis (corner arc / S-spline, not the 24° arc) when both dx and dy exceed this
    public double OffAxisThreshold => 0.5 * _k;
}
