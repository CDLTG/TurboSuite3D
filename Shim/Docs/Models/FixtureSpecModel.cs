using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.Models;

public class FixtureSpecModel : ViewModelBase
{
    private bool _isSelected = true;
    private string _localPdfPath = string.Empty;
    private bool _isDefaultPdf;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string LocalPdfPath
    {
        get => _localPdfPath;
        set
        {
            if (SetProperty(ref _localPdfPath, value))
                OnPropertyChanged(nameof(HasLocalPdf));
        }
    }

    public bool HasLocalPdf => !string.IsNullOrEmpty(LocalPdfPath);

    /// <summary>
    /// True when LocalPdfPath was loaded from the global default (keyed by CatalogNumber).
    /// </summary>
    public bool IsDefaultPdf
    {
        get => _isDefaultPdf;
        set => SetProperty(ref _isDefaultPdf, value);
    }

    public string TypeMark { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string DataSheetUrl { get; set; } = string.Empty;
    public string CatalogNumber { get; set; } = string.Empty;
    public ElementId SymbolId { get; set; } = ElementId.InvalidElementId;
}
