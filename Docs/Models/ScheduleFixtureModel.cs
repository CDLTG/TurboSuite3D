using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.Models;

public class ScheduleFixtureModel : ViewModelBase
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string TypeMark { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;

    // Catalog Number1–6 joined with " | "
    public string CatalogNumber { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;
    public string Description1 { get; set; } = string.Empty;
    public string Description2 { get; set; } = string.Empty;

    // Mechanical section — only rendered when non-empty
    public string Finish { get; set; } = string.Empty;
    public string Listings { get; set; } = string.Empty;
    public string Mounting { get; set; } = string.Empty;

    // Electrical section — only rendered when non-empty
    public string Dimming { get; set; } = string.Empty;
    public string Watts { get; set; } = string.Empty;
    public string Volts { get; set; } = string.Empty;

    // Photometric section — only rendered when non-empty
    public string Lumens { get; set; } = string.Empty;
    public string CCT { get; set; } = string.Empty;
    public string CRI { get; set; } = string.Empty;

    /// <summary>
    /// Non-empty schedule notes (from Schedule Note1–6 parameters).
    /// </summary>
    public string[] ScheduleNotes { get; set; } = [];
}
