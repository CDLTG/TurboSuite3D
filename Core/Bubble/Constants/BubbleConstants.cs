namespace TurboSuite.Bubble.Constants;

/// <summary>
/// TurboBubble's scale-independent constants: type names, angles, segment counts, epsilons. Every
/// paper-relative length lives in <see cref="BubbleGeometry"/>, scaled to the placement view — none
/// belongs here. Public (not internal) because it lives in TurboSuite.Core and the Bubble shim
/// consumes it across the assembly boundary. Tag/detail families are found by TurboSuite Role (see
/// Core Roles), not by the name constants that used to live here.
/// </summary>
public static class BubbleConstants
{
    // Unit conversion
    public const double InchesToFeet = 1.0 / 12.0;

    // Remote-switchleg tag type names (the RemoteSwitchlegTag family carries both).
    public const string RemoteSwitchlegTypeRight = "Switchleg Right";
    public const string RemoteSwitchlegTypeLeft = "Switchleg Left";

    // Vertical electrical wire arc (lengths in BubbleGeometry.ElectricalVerticalArc*)
    public const double ElectricalVerticalArcSweepDeg = 145.0; // arc sweep in degrees
    public const int ElectricalVerticalArcSegments = 3;        // number of segments to approximate arc

    // Ceiling Fan wire start (V1), seated on the base of the fan. Real-world, NOT view-scaled: ceiling
    // fan families deliberately draw their symbol as model geometry (no nested annotation) so the fan
    // shows its true size at any scale, and V1 is anchored to that unscaled body. The tag, V2 and the
    // elbow stand off in paper space with the (scaled) tag, so they stay in BubbleGeometry.
    public const double CeilingFanV1XOffsetFt = 4.1875 * InchesToFeet;    // 4 3/16" along localX
    public const double CeilingFanV1YOffsetFt = 1.78125 * InchesToFeet;   // 1 25/32" along localY

    // Chandelier (decorative pendant) — diagonal-corner switchleg placement, classified by TurboSuite Role.
    public const double ChandelierBubbleAngleDegrees = 33.0;

    // Picture lights — linear wall fixtures whose plan symbol is symmetric along the wall but
    // extends ENTIRELY away from the wall (asymmetric perpendicular; origin sits on the wall-side
    // edge). Both the 2D and 3D families are geometrically identical in wall-normal terms, so one
    // wall-aware placement path serves both. Routed ahead of the shared vertical-face path so
    // sconces/mirrors are untouched. Classified by TurboSuite Role. Offsets: BubbleGeometry.PictureLight*.

    // Linear Feed (static driver tag) — used when "Enable dynamic driver tags" is OFF
    // and the fixture is line-based with a remote power supply. The tag/detail families are found
    // by TurboSuite Role (LinearFeedTag / LinearFeedDetail); this is the tag's default type name.
    public const string LinearFeedTagDefaultType = "SwitchID";

    // Rotation/normal comparison threshold
    public const double RotationEpsilon = 0.001;
    public const double NormalEpsilon = 0.001;
}
