namespace TurboSuite.Shared.Models;

public class GeneralSettings
{
    public bool ShowCircuitCommentsDialog { get; set; } = true;

    public static GeneralSettings CreateDefaults() => new();
}
