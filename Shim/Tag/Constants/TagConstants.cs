namespace TurboSuite.Tag.Constants;

/// <summary>
/// TurboTag's scale-independent constants: names, text-length thresholds, and the real-world run
/// adjacency tolerance. Every paper-relative placement length lives in Core's <see cref="TagGeometry"/>,
/// scaled to the view being tagged — none belongs here.
/// </summary>
internal static class TagConstants
{
    public const string RunLengthParamName = "Run Length";
    // 1/32" expressed in feet — tolerance for endpoint adjacency when grouping continuous linear runs.
    public const double LinearContinuityToleranceFeet = (1.0 / 32.0) / 12.0;
    public const int ShortTextThreshold = 2;
    public const int MediumTextThreshold = 3;

    public const string KeypadTwoGangTypeName = "2. Two Gang";
    public const string KeypadTwoGangParamName = "Two Gang";
}
