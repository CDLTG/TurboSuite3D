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

    /// <summary>
    /// Name prefix identifying the firm's lighting view templates (AL_Floor Plan, AL_RCP,
    /// AL_Section, AL_Elevation, …). The Toposolid-off sweep targets every template whose name
    /// starts with this, so section/elevation templates that set RVT Links to "By Host View"
    /// carry a Toposolid-off host state into the views they're applied to.
    /// </summary>
    public const string LightingTemplatePrefix = "AL_";

    /// <summary>Suffix for generated floor plan view names: "{NN} - Floor - Lighting".</summary>
    public const string FloorViewSuffix = "Floor - Lighting";

    /// <summary>Suffix for generated RCP view names: "{NN} - RCP - Lighting".</summary>
    public const string RcpViewSuffix = "RCP - Lighting";
}
