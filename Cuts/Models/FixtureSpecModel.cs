using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Cuts.Models;

public class FixtureSpecModel : ViewModelBase
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string TypeMark { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string DataSheetUrl { get; set; } = string.Empty;
    public ElementId SymbolId { get; set; } = ElementId.InvalidElementId;
}
