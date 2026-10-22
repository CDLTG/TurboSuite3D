using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble.Constants;

/// <summary>
/// Every paper-relative length TurboBubble places with, scaled to the placement view. Each member is
/// its 1/4" = 1'-0" reference value × <see cref="PlanScale.K"/>, so the switchleg cluster prints the
/// same size at any view scale and is byte-identical at 1/4" (K = 1). Built once per command run from
/// the view being placed into (<c>PlanScaleHelper.Of(view)</c>).
///
/// Values keep their original <c>v * InchesToFeet</c> form, with K applied last: <c>v / 12.0</c> rounds
/// differently for many of them, which would move 1/4" output. Lengths only — dimensionless and
/// real-world constants stay in <see cref="BubbleConstants"/>.
/// </summary>
public sealed class BubbleGeometry
{
    private const double In = BubbleConstants.InchesToFeet;

    private readonly double _k;

    public BubbleGeometry(PlanScale scale) => _k = scale.K;

    // Symbol dimension fallbacks, for when measurement fails (a measured extent is already at view scale)
    public double DefaultSymbolSizeFt => 4.5 * In * _k;
    public double MinSymbolWidthFt => 4.0 * In * _k;

    // Tag width by character count (4+ chars are measured from the tag's bounding box)
    public double TagWidth1CharFt => 6.25 * In * _k;
    public double TagWidth2CharsFt => 7.75 * In * _k;
    public double TagWidth3CharsFt => 11.15 * In * _k;

    // Wire geometry offsets
    public double WireHorizontalOffsetFt => 0.613 * In * _k;
    public double WireVerticalOffsetFt => 0.65 * In * _k;
    public double WireToSymbolGapFt => 4.215 * In * _k;
    public double WireToSymbolGapHorizontalFt => 4.55 * In * _k;
    public double WireToSymbolGapWallSconceFt => 0.5 * In * _k;
    public double WireElbowOffsetFt => 3.56 * In * _k;

    // Wire end offsets (the wall-sconce one is further multiplied by the fixture's Scale Factor)
    public double WireOffsetEndInitialFt => 0.5 * In * _k;
    public double WireOffsetEndFinalFt => 1.0 * In * _k;
    public double WireOffsetEndWallSconceFt => 2.5 * In * _k;

    // Wall-face fixtures (sconces/mirrors): tag past the symbol along the wall, and off the wall
    public double WallTagAlongWallGapFt => 8.25 * In * _k;
    public double WallTagOffWallFt => 5.5 * In * _k;

    // Line-based fixtures: tag along the fixture from the connector, and perpendicular off it
    public double LineBasedTagAlongFixtureFt => 14.0 * In * _k;
    public double LineBasedTagPerpendicularFt => 6.25 * In * _k;

    public double ElectricalVerticalTagOffsetFt => 10.0 * In * _k;  // 10" tag offset along localY

    // Vertical wire arc: radius 4.5", center at (3.78125", 3.75"), starting at (4", 0") relative to the
    // fixture origin (where the connector sits) — the start is both the arc's first point and the
    // wire's seated start vertex.
    public double ElectricalVerticalArcRadiusFt => 4.5 * In * _k;
    public double ElectricalVerticalArcCenterXFt => 3.78125 * In * _k; // along localX
    public double ElectricalVerticalArcCenterYFt => 3.75 * In * _k;    // along localY
    public double ElectricalVerticalArcStartXFt => 4.0 * In * _k;      // along localX

    // Default Electrical Fixture switchleg (left/right placement)
    public double ElectricalMidpointOffsetFt => 6.0 * In * _k;   // 6" from origin to annotation center along localY
    public double ElectricalTagOffsetFt => 12.5 * In * _k;       // 12.5" tag offset along localX
    public double ElectricalV2XOffsetFt => 3.47 * In * _k;       // V2 along localX
    public double ElectricalV2YOffsetFt => 1.56 * In * _k;       // V2 perpendicular
    public double ElectricalV3XOffsetFt => 6.9 * In * _k;        // V3 along localX
    public double ElectricalV3YOffsetFt => 3.09 * In * _k;       // V3 perpendicular
    public double ElectricalWireStartOffsetFt => 3.0 * In * _k;  // wire start perpendicular offset (SetVertex)

    // Ceiling Fan fixed switchleg placement (no user flip prompt). The fan symbol itself is model
    // geometry and does NOT scale; these stand off in paper space with the tag. The wire start (V1),
    // seated on the fan base, is real-world and lives in BubbleConstants.
    public double CeilingFanTagXOffsetFt => 15.0 * In * _k;     // 1'-3" along localX
    public double CeilingFanTagYOffsetFt => 7.625 * In * _k;    // 0'-7 5/8" along localY
    public double CeilingFanV2XOffsetFt => 9.0625 * In * _k;    // 9 1/16" along localX
    public double CeilingFanV2YOffsetFt => 1.75 * In * _k;      // 1 3/4" along localY

    // Chandelier (decorative pendant) — diagonal-corner switchleg placement
    public double ChandelierBubbleOffsetExtensionFt => 8.0 * In * _k; // push bubble outward past crosshair tip along the diagonal
    public double ChandelierBubbleHorizontalNudgeFt => 0.75 * In * _k; // extra horizontal-only push away from fixture (added along the corner's X sign)
    public double ChandelierWireV2GapFt => 1.59 * In * _k;            // V2 distance past symbol edge along diagonal
    public double ChandelierWireV2PerpKickFt => 3.50 * In * _k;       // V2 perpendicular kick (creates arc curvature)
    public double ChandelierWireElbowOffsetFt => 3.56 * In * _k;      // V3 inset from bubble along diagonal (elbow)

    // Picture lights — switchleg geometry in the wall frame (X along wall, Y = wall normal away from
    // wall), derived from a hand-drawn ideal (TurboSpike read-back): the bubble stands off past the
    // MEASURED room-side depth so it clears the bar and sits in open room (the 2D look), and the wire
    // exits the bar end near the room side then arcs down to the bubble.
    public double PictureLightTagAlongWallGapFt => 8.75 * In * _k; // bubble past the bar end
    public double PictureLightTagClearanceFt => 4.25 * In * _k;    // bubble/elbow past the room-side edge (added to roomDepth)
    // v1/v2 wire depths are anchored to the MEASURED room-side edge (roomDepth), inset back toward the
    // wall — so the whole arc keeps its shape and simply translates outward for a deeper symbol, rather
    // than staying pinned near the wall while the bubble marches out. Insets derived from the reference
    // family (spiked roomDepth 4.487") to reproduce the tuned look byte-for-byte: 4.487-3.7 and 4.487-2.9.
    public double PictureLightWireEndInsetFt => 0.787 * In * _k;   // v1 inset from the room-side edge
    public double PictureLightWireMidInsetFt => 1.587 * In * _k;   // v2 inset (deeper => bows the arc)

    // Tag placement offsets
    public double TagOffsetVerticalFt => 5.25 * In * _k;
    public double TagOffsetHorizontalFt => 8.75 * In * _k;
    public double TagXOffsetFt => 9.0 * In * _k;
    public double RemoteSwitchlegExtraXOffsetFt => 5.15625 * In * _k;

    // Linear Feed (static driver tag). Detail leader: placement line offset 1" perpendicular from the
    // connector (default "down" side), running along the fixture for a placeholder length to be dialed
    // in. Tag: past the end of the detail leader (parallel) and off the fixture line (perpendicular).
    public double LinearFeedDetailPerpOffsetFt => 1.0 * In * _k;
    public double LinearFeedDetailLengthFt => 12.0 * In * _k;
    public double LinearFeedTagParallelGapFt => 6.5 * In * _k;
    public double LinearFeedTagPerpOffsetFt => 5.0 * In * _k;
}
