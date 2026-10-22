namespace TurboSuite.Shared.Helpers;

/// <summary>
/// The paper-scale factor of the plan view TurboSuite places into. Every placement offset was tuned
/// at 1/4" = 1'-0" (<see cref="ReferenceScale"/>); a paper-relative length multiplied by <see cref="K"/>
/// keeps the same printed size at any other view scale. K is exactly 1.0 at 1/4", so reference-scale
/// output is byte-identical to the unscaled constants.
///
/// A sealed class rather than a struct so there is no default instance with K = 0, and a type rather
/// than a bare double so an int <c>View.Scale</c> can't be passed where a factor is expected.
/// </summary>
public sealed class PlanScale
{
    /// <summary>1/4" = 1'-0" — the view scale every placement constant was tuned at.</summary>
    public const int ReferenceScale = 48;

    /// <summary>K = 1.0 — the scale every legacy constant describes.</summary>
    public static readonly PlanScale Reference = new(1.0);

    /// <summary>View scale ÷ <see cref="ReferenceScale"/>: 1/4" → 1.0, 3/16" → 4/3, 1/8" → 2.0.</summary>
    public double K { get; }

    private PlanScale(double k) => K = k;

    /// <summary>
    /// Builds the factor from a Revit <c>View.Scale</c> (the 1:N denominator). A non-positive scale
    /// can't come from a real view; it falls back to the reference rather than zeroing every offset.
    /// </summary>
    public static PlanScale FromViewScale(int viewScale) =>
        viewScale <= 0 ? Reference : new PlanScale(viewScale / (double)ReferenceScale);
}
