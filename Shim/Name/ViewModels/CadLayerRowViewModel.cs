#nullable disable
using System;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

/// <summary>
/// One row of the folded-in VG → Imported Categories checklist: a single linked-CAD layer with a live
/// visibility checkbox. Toggling <see cref="IsVisible"/> asks the host ViewModel to raise a
/// <see cref="Services.SetLayerVisibilityRequest"/>; if the shared external event is busy the toggle is
/// declined and the checkbox reverts (so the UI never drifts from the view's real state).
/// </summary>
public class CadLayerRowViewModel : ViewModelBase
{
    private readonly Func<CadLayerRowViewModel, bool> _requestToggle;
    private bool _isVisible;
    private bool _suppress;

    public string FileName { get; }
    public string LayerName { get; }
    public ElementId SubId { get; }

    public CadLayerRowViewModel(string fileName, string layerName, ElementId subId, bool isVisible,
        Func<CadLayerRowViewModel, bool> requestToggle)
    {
        FileName = fileName;
        LayerName = layerName;
        SubId = subId;
        _isVisible = isVisible;
        _requestToggle = requestToggle;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
            if (_suppress) return; // programmatic revert — don't re-raise

            if (!_requestToggle(this))
            {
                // Shared event busy: undo the optimistic checkbox change.
                _suppress = true;
                IsVisible = !value;
                _suppress = false;
            }
        }
    }
}
