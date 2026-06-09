#nullable disable
using System;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Setup.ViewModels;

/// <summary>Stage 1 row: one level in the chosen architectural link.</summary>
public class LevelRowViewModel : ViewModelBase
{
    private bool _isIncluded;
    private bool _isMain;

    public LevelRowViewModel(ElementId sourceLevelId, string name, double elevation)
    {
        SourceLevelId = sourceLevelId;
        Name = name;
        Elevation = elevation;
    }

    public ElementId SourceLevelId { get; }

    public string Name { get; }

    /// <summary>Elevation in internal units (feet); used for sorting and the default-Main rule.</summary>
    public double Elevation { get; }

    /// <summary>Elevation shown to the user, rounded to feet with two decimals.</summary>
    public string ElevationDisplay => $"{Elevation:0.00} ft";

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public bool IsMain
    {
        get => _isMain;
        set
        {
            if (SetProperty(ref _isMain, value) && value)
                MainRequested?.Invoke(this);
        }
    }

    /// <summary>Raised when this row becomes Main, so the parent can clear the others.</summary>
    public event Action<LevelRowViewModel> MainRequested;

    /// <summary>Clears Main without re-raising <see cref="MainRequested"/>.</summary>
    public void ClearMainSilently()
    {
        if (_isMain)
            SetProperty(ref _isMain, false, nameof(IsMain));
    }
}
