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
    public const string Voltage = "Voltage";
    public const string MaximumFixtures = "Maximum Fixtures";
    public const string Power = "Power";
    public const string SubDriverPower = "Sub-Driver Power";
    public const string ReelLength = "Reel Length";
    public const string ChannelLength = "Channel Length";
    public const string RemotePowerSupply = "Remote Power Supply";
    public const string DataSheetUrl = "Data Sheet URL";
    public const string Manufacturer = "Manufacturer";
    public const string CatalogNumber1 = "Catalog Number1";

    // Circuit parameters
    public const string LoadClassification = "Load Classification";
    public const string LoadClassificationAbbreviation = "Load Classification Abbreviation";

    // Panel parameters
    public const string CircuitNaming = "Circuit Naming";
    public const string CircuitPrefix = "Circuit Prefix";
    public const string CircuitPrefixSeparator = "Circuit Prefix Separator";
}
