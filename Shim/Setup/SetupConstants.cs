namespace TurboSuite.Setup;

/// <summary>
/// Hardcoded configuration for TurboSetup. These are firm standards baked into the
/// host project template — never user-overridable, so no Settings UI / ExtensibleStorage.
/// </summary>
internal static class SetupConstants
{
    /// <summary>View template applied to every generated Floor Plan view.</summary>
    public const string FloorPlanViewTemplateName = "AL_Floor Plan";

    /// <summary>View template applied to every generated RCP (ceiling plan) view.</summary>
    public const string RcpViewTemplateName = "AL_RCP";

    /// <summary>Suffix for generated floor plan view names: "{NN} - Floor - Lighting".</summary>
    public const string FloorViewSuffix = "Floor - Lighting";

    /// <summary>Suffix for generated RCP view names: "{NN} - RCP - Lighting".</summary>
    public const string RcpViewSuffix = "RCP - Lighting";
}
