using TurboSuite.Shared.Helpers;

namespace TurboSuite.Tag.Constants;

/// <summary>
/// Every paper-relative length TurboTag places with, scaled to the placement view. Each member is its
/// 1/4" = 1'-0" reference value × <see cref="PlanScale.K"/>, so a tag sits the same printed distance from
/// its fixture at any view scale and is byte-identical at 1/4" (K = 1). Built once per command run from
/// the view being tagged (<c>PlanScaleHelper.Of(view)</c>). Public because the shim (TurboTag, and
/// TurboDriver's split-fixture tags) consumes it across the assembly boundary.
///
/// Values keep their original <c>v / 12.0</c> form, with K applied last: <c>v * (1.0 / 12.0)</c> rounds
/// differently for several of them, which would move 1/4" output. Lengths only — names, text-length
/// thresholds and the real-world run-adjacency tolerance stay in the shim's <c>TagConstants</c>.
/// </summary>
public sealed class TagGeometry
{
    private readonly double _k;

    public TagGeometry(PlanScale scale) => _k = scale.K;

    // Fallback symbol extent, for when measurement fails (a measured extent is already at view scale)
    public double DefaultSymbolSizeFeet => 4.5 / 12.0 * _k;

    // Point fixtures: gap past the symbol edge (Up/Down), and past symbol + half the tag (Right/Left)
    public double VerticalOffsetFeet => 4.25 / 12.0 * _k;
    public double OffsetMarginRightFeet => 2.35 / 12.0 * _k;
    public double OffsetMarginLeftFeet => 1.7 / 12.0 * _k;

    // Estimated tag widths by type-mark length (longer marks are measured from the tag's bounding box)
    public double DefaultTagWidthShort => 7.75 / 12.0 * _k;
    public double DefaultTagWidthMedium => 11.15 / 12.0 * _k;

    // Line-based fixtures: tag offset perpendicular to the fixture line (also TurboDriver's split tags)
    public double LinearOffsetFeet => 5.0 / 12.0 * _k;

    // Keypads: tag offset out of the wall from the keypad origin
    public double KeypadOffsetFeet => 9.0 / 12.0 * _k;
}
