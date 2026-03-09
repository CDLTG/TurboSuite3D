using System;
using System.Collections.Generic;

namespace TurboSuite.Bubble.Constants;

/// <summary>
/// All geometry constants used by TurboBubble.
/// </summary>
internal static class BubbleConstants
{
    // Unit conversion
    public const double InchesToFeet = 1.0 / 12.0;

    // Required families
    public const string SwitchlegTagFamily = "AL_Tag_Lighting Fixture (Switchleg)";
    public const string RemoteSwitchlegTagFamily = "AL_Tag_Lighting Fixture (Remote Switchleg)";
    public const string RemoteSwitchlegTypeRight = "Switchleg Right";
    public const string RemoteSwitchlegTypeLeft = "Switchleg Left";

    // Symbol dimension defaults (in feet)
    public const double DefaultSymbolSizeFt = 4.5 * InchesToFeet;
    public const double MinSymbolWidthFt = 4.0 * InchesToFeet;

    // Tag width by character count (in feet)
    public const double TagWidth1CharFt = 6.25 * InchesToFeet;
    public const double TagWidth2CharsFt = 7.75 * InchesToFeet;
    public const double TagWidth3CharsFt = 11.15 * InchesToFeet;

    // Wire geometry offsets (in feet)
    public const double WireHorizontalOffsetFt = 0.613 * InchesToFeet;
    public const double WireVerticalOffsetFt = 0.65 * InchesToFeet;
    public const double WireToSymbolGapFt = 4.215 * InchesToFeet;
    public const double WireToSymbolGapHorizontalFt = 4.55 * InchesToFeet;
    public const double WireToSymbolGapWallSconceFt = 0.5 * InchesToFeet;
    public const double WireElbowOffsetFt = 3.56 * InchesToFeet;

    // Wire end offset constants
    public const double WireOffsetEndInitialFt = 0.5 * InchesToFeet;
    public const double WireOffsetEndFinalFt = 1.0 * InchesToFeet;
    public const double WireOffsetEndWallSconceFt = 2.5 * InchesToFeet;

    // Electrical Fixture switchleg
    public const string ElectricalSwitchlegTagFamily = "AL_Tag_Electrical Fixture (Switchleg)";

    // Electrical Fixture families with vertical (up/down) switchleg placement
    public static readonly HashSet<string> ElectricalVerticalFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL_Electrical Fixture_Exhaust (Hosted)",
        "AL_Electrical Fixture_Exhaust",
        "AL_Electrical Fixture_Fireplace Igniter",
        "Exhaust",
        "Fireplace Igniter"
    };
    public const double ElectricalVerticalTagOffsetFt = 10.0 * InchesToFeet;  // 10" tag offset along localY

    // Vertical wire arc parameters (in inches, converted to feet)
    // Arc circle: radius 4.5", center at (3.78125", 3.75") relative to fixture origin
    public const double ElectricalVerticalArcRadiusFt = 4.5 * InchesToFeet;
    public const double ElectricalVerticalArcCenterXFt = 3.78125 * InchesToFeet; // along localX
    public const double ElectricalVerticalArcCenterYFt = 3.75 * InchesToFeet;    // along localY
    public const double ElectricalVerticalArcSweepDeg = 145.0; // arc sweep in degrees
    public const int ElectricalVerticalArcSegments = 3;        // number of segments to approximate arc

    // Default Electrical Fixture switchleg (left/right placement)
    public const double ElectricalMidpointOffsetFt = 6.0 * InchesToFeet;   // 6" from origin to annotation center along localY
    public const double ElectricalTagOffsetFt = 12.5 * InchesToFeet;       // 12.5" tag offset along localX
    public const double ElectricalV2XOffsetFt = 3.47 * InchesToFeet;      // V2 along localX
    public const double ElectricalV2YOffsetFt = 1.56 * InchesToFeet;      // V2 perpendicular
    public const double ElectricalV3XOffsetFt = 6.9 * InchesToFeet;       // V3 along localX
    public const double ElectricalV3YOffsetFt = 3.09 * InchesToFeet;      // V3 perpendicular
    public const double ElectricalWireStartOffsetFt = 3.0 * InchesToFeet; // wire start perpendicular offset (SetVertex)

    // Special family names
    public const string WallSconceFamily = "AL_Decorative_Wall Sconce (Hosted)";

    // Tag placement offsets (in feet)
    public const double TagOffsetVerticalFt = 5.25 * InchesToFeet;
    public const double TagOffsetHorizontalFt = 8.75 * InchesToFeet;
    public const double TagXOffsetFt = 9.0 * InchesToFeet;
    public const double RemoteSwitchlegExtraXOffsetFt = 5.15625 * InchesToFeet;

    // Rotation/normal comparison threshold
    public const double RotationEpsilon = 0.001;
    public const double NormalEpsilon = 0.001;
}
