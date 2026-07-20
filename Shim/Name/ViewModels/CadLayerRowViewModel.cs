#nullable disable
using System;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

/// <summary>The role a linked-CAD layer can be tagged with in the layer table.</summary>
public enum LayerRole { Wall, Door, Area, Name, Height }

/// <summary>
/// One row of the folded-in VG → Imported Categories checklist AND the config surface: a linked-CAD layer with
/// a live visibility checkbox plus W/D/A (region-gen) and Name/Ht (text-scope) role toggles. Toggling anything
/// asks the host ViewModel (via callbacks) to act in a valid API context — visibility flips the view live,
/// region-gen roles paint the layer red, and every role writes the layer's <c>(file,layer)</c> into the
/// matching scope. Programmatic changes (single-select Name/Ht enforcement, initial seeding) go through the
/// <c>*Silently</c> setters so they don't re-enter the callbacks.
/// </summary>
public class CadLayerRowViewModel : ViewModelBase
{
    private readonly Func<CadLayerRowViewModel, bool> _requestToggle;
    private readonly Action<CadLayerRowViewModel, LayerRole, bool> _onRole;

    private bool _isVisible;
    private bool _suppressVisibility;
    private bool _suppressRole;

    private bool _isWall, _isDoor, _isArea, _isName, _isHeight;

    public string FileName { get; }
    public string LayerName { get; }
    public ElementId SubId { get; }

    public CadLayerRowViewModel(string fileName, string layerName, ElementId subId, bool isVisible,
        Func<CadLayerRowViewModel, bool> requestToggle,
        Action<CadLayerRowViewModel, LayerRole, bool> onRole)
    {
        FileName = fileName;
        LayerName = layerName;
        SubId = subId;
        _isVisible = isVisible;
        _requestToggle = requestToggle;
        _onRole = onRole;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
            if (_suppressVisibility) return; // programmatic (revert / auto-show) — don't re-raise

            if (!_requestToggle(this))
            {
                _suppressVisibility = true;
                IsVisible = !value; // shared event busy: undo the optimistic checkbox change
                _suppressVisibility = false;
            }
        }
    }

    /// <summary>Set the visibility checkbox without raising a toggle request (used when a role tag auto-shows a
    /// hidden layer, since the paint request already un-hides it in the same transaction).</summary>
    public void SetVisibleSilently(bool visible)
    {
        _suppressVisibility = true;
        IsVisible = visible;
        _suppressVisibility = false;
    }

    // ── Role toggles ──

    public bool IsWall { get => _isWall; set => SetRole(ref _isWall, value, LayerRole.Wall); }
    public bool IsDoor { get => _isDoor; set => SetRole(ref _isDoor, value, LayerRole.Door); }
    public bool IsArea { get => _isArea; set => SetRole(ref _isArea, value, LayerRole.Area); }
    public bool IsName { get => _isName; set => SetRole(ref _isName, value, LayerRole.Name); }
    public bool IsHeight { get => _isHeight; set => SetRole(ref _isHeight, value, LayerRole.Height); }

    /// <summary>True while any region-gen role is set — the row that carries the red preview.</summary>
    public bool IsRegionGenTagged => _isWall || _isDoor || _isArea;

    private void SetRole(ref bool field, bool value, LayerRole role)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(RolePropertyName(role));
        if (role is LayerRole.Wall or LayerRole.Door or LayerRole.Area)
            OnPropertyChanged(nameof(IsRegionGenTagged));
        if (_suppressRole) return;
        _onRole?.Invoke(this, role, value);
    }

    /// <summary>Set a role without raising the callback (single-select enforcement + initial seeding).</summary>
    public void SetRoleSilently(LayerRole role, bool value)
    {
        _suppressRole = true;
        switch (role)
        {
            case LayerRole.Wall: IsWall = value; break;
            case LayerRole.Door: IsDoor = value; break;
            case LayerRole.Area: IsArea = value; break;
            case LayerRole.Name: IsName = value; break;
            case LayerRole.Height: IsHeight = value; break;
        }
        _suppressRole = false;
    }

    private static string RolePropertyName(LayerRole role) => role switch
    {
        LayerRole.Wall => nameof(IsWall),
        LayerRole.Door => nameof(IsDoor),
        LayerRole.Area => nameof(IsArea),
        LayerRole.Name => nameof(IsName),
        _ => nameof(IsHeight),
    };
}
