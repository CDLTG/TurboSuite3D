using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.Models;

public class RPSScheduleModel : ViewModelBase
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string TypeMark { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;

    // Catalog Number1–6 joined with " | "
    public string CatalogNumber { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;
    public string Description1 { get; set; } = string.Empty;
    public string Description2 { get; set; } = string.Empty;

    // Capacity section — only rendered when non-empty
    public string Power { get; set; } = string.Empty;
    public string SubDriverPower { get; set; } = string.Empty;
    public string MaxFixtures { get; set; } = string.Empty;

    // Electrical section — only rendered when non-empty
    public string Dimming { get; set; } = string.Empty;
    public string Voltage { get; set; } = string.Empty;

    /// <summary>
    /// Non-empty schedule notes (from Schedule Notes1–6 parameters).
    /// </summary>
    public string[] ScheduleNotes { get; set; } = [];
}
