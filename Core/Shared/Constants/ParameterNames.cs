namespace TurboSuite.Shared.Constants;

/// <summary>
/// Centralized custom Revit parameter names. Add new entries here rather than
/// passing string literals to <c>LookupParameter</c>, so renames are one-touch
/// and every callsite is grep-discoverable.
/// </summary>
public static class ParameterNames
{
    // Lighting Device / Fixture instance parameters
    public const string SwitchId = "Switch ID";
    public const string ScaleFactor = "Scale Factor";
    public const string LinearLength = "Linear Length";
    public const string LinearPower = "Linear Power";
    public const string TwoGang = "Two Gang";
    public const string Orientation = "Orientation";
    public const string Angle = "Angle";

    /// <summary>
    /// Yes/No: this device talks over RF rather than the wired link, so it rides a Clear Connect
    /// Type A link and consumes that link's device budget instead of a QS link's.
    ///
    /// Read type-first-with-instance-override, exactly like <see cref="TwoGang"/> — wired vs wireless
    /// is a property of the model, so it belongs on the type, but a family may expose it per
    /// instance. Deliberately generic rather than keypad-specific: visor receivers, shades and
    /// sensors are the same question, and so is a repeater declaring itself instead of being matched
    /// by family name.
    ///
    /// Absent reads as false, which is exactly the wired-everything behaviour that shipped before
    /// this existed — so the read path is inert until the families carry the parameter.
    /// </summary>
    public const string Wireless = "Wireless";

    /// <summary>
    /// The one designer-set instance parameter grouping fixtures into control zones for a zone/loop-based
    /// control subsystem (DMX today, DALI next). A blank value is "unassigned". Protocol-agnostic:
    /// <see cref="DimmingProtocol"/> routes which subsystem owns a fixture, and this names the zone
    /// within it. Template shared param, bound per-family to DMX/DALI fixtures only.
    /// <c>DmxParameterNames.ControlZone</c> aliases this so the two subsystems key on one string.
    /// </summary>
    public const string ControlZone = "Control Zone";

    /// <summary>
    /// TurboDALI's per-circuit design address — the <c>L{loop}-{load##}</c> label (e.g. <c>L2-01</c>) written
    /// back to every element on a DALI circuit (its tape/downlight fixtures AND the remote driver/decoder
    /// device). A shared, instance-bound, taggable String param authored manually in the firm shared-param
    /// file (Phase 0), bound to <b>both Lighting Fixtures and Lighting Devices</b> so the driver device can
    /// carry the label too. A design/commissioning label, not a hardware DALI short address (0–63).
    /// </summary>
    public const string DaliAddress = "DALI Address";

    // Lighting Device / Fixture type parameters
    public const string TypeMark = "Type Mark";
    public const string DimmingProtocol = "Dimming Protocol";
    public const string DimmingRange = "Dimming Range";
    public const string Voltage = "Voltage";
    public const string MaximumFixtures = "Maximum Fixtures";
    public const string Power = "Power";
    public const string PowerPerLength = "Power Per Length";
    public const string SubDriverPower = "Sub-Driver Power";
    public const string DeratingFactor = "Derating Factor";
    public const string RemotePowerSupply = "Remote Power Supply";
    public const string DataSheetUrl = "Data Sheet URL";
    public const string Manufacturer = "Manufacturer";
    public const string CatalogNumber1 = "Catalog Number1";
    public const string CatalogQty1 = "Catalog Qty1";

    // TurboSchedule spec editor — Identity / Mechanical
    public const string Classification = "Classification";
    public const string Description2 = "Description2";
    public const string Finish1 = "Finish1";
    public const string Finish2 = "Finish2";
    public const string ListingsAndRatings = "Listings and Ratings";
    public const string Mounting = "Mounting";
    public const string CeilingThickness = "Ceiling Thickness";

    // TurboSchedule spec editor — Photometric
    public const string Lumens = "Lumens";
    public const string LumenEfficacy = "Lumen Efficacy";
    public const string BeamAngle = "Beam Angle";
    public const string Cbcp = "Center Beam Candle Power (CBCP)";
    public const string Cct = "Correlated Color Temperature (CCT)";
    public const string Cri = "Color Rendering Index (CRI)";
    public const string Sdcm = "Standard Deviation Color Matching (SDCM)";
    public const string Rf = "Color Fidelity (Rf)";
    public const string Rg = "Color Gamut (Rg)";

    // Circuit parameters
    public const string LoadClassification = "Load Classification";
    public const string LoadClassificationAbbreviation = "Load Classification Abbreviation";

    // Panel parameters
    public const string CircuitNaming = "Circuit Naming";
    public const string CircuitPrefix = "Circuit Prefix";
    public const string CircuitPrefixSeparator = "Circuit Prefix Separator";
}
