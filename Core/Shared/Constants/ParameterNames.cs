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
