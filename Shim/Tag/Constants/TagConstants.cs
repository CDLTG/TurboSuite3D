namespace TurboSuite.Tag.Constants;

internal static class TagConstants
{
    public const string RunLengthParamName = "Run Length";
    // 1/32" expressed in feet — tolerance for endpoint adjacency when grouping continuous linear runs.
    public const double LinearContinuityToleranceFeet = (1.0 / 32.0) / 12.0;
    public const double DefaultSymbolSizeFeet = 4.5 / 12.0;
    public const double OffsetMarginRightFeet = 2.35 / 12.0;
    public const double OffsetMarginLeftFeet = 1.7 / 12.0;
    public const double LinearOffsetFeet = 5.0 / 12.0;
    public const double VerticalOffsetFeet = 4.25 / 12.0;
    public const double DefaultTagWidthShort = 7.75 / 12.0;
    public const double DefaultTagWidthMedium = 11.15 / 12.0;
    public const int ShortTextThreshold = 2;
    public const int MediumTextThreshold = 3;

    public const double KeypadOffsetFeet = 9.0 / 12.0;
    public const string KeypadTwoGangTypeName = "2. Two Gang";
    public const string KeypadTwoGangParamName = "Two Gang";
}
