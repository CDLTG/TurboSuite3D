#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;
using MediaBrush = System.Windows.Media.Brush;
using MediaSolidBrush = System.Windows.Media.SolidColorBrush;
using MediaColor = System.Windows.Media.Color;
using MediaDashes = System.Windows.Media.DoubleCollection;

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

    // Per-pattern on/off feet arrays (shared, read once in a valid API context) — drives the preview dash array.
    private readonly IReadOnlyDictionary<ElementId, double[]> _dashArrays;

    private OverrideGraphicSettings _lineOverride;
    /// <summary>Snapshot of the layer's current per-view graphic override — seeds the Line Graphics flyout and,
    /// mutated + written back, is updated here on a successful apply (TurboName-12). Read once in a valid API
    /// context (view overrides aren't safe to query off the Revit thread). Setting it re-renders the preview
    /// swatch.</summary>
    public OverrideGraphicSettings LineOverride
    {
        get => _lineOverride;
        set { _lineOverride = value; RecomputePreview(); }
    }

    public CadLayerRowViewModel(string fileName, string layerName, ElementId subId, bool isVisible,
        OverrideGraphicSettings lineOverride, IReadOnlyDictionary<ElementId, double[]> dashArrays,
        Func<CadLayerRowViewModel, bool> requestToggle,
        Action<CadLayerRowViewModel, LayerRole, bool> onRole)
    {
        FileName = fileName;
        LayerName = layerName;
        SubId = subId;
        _isVisible = isVisible;
        _dashArrays = dashArrays ?? new Dictionary<ElementId, double[]>();
        _requestToggle = requestToggle;
        _onRole = onRole;
        LineOverride = lineOverride; // sets field + renders the initial swatch
    }

    // ── Line-preview swatch (TurboName-12): color / weight-as-thickness / pattern-as-dash, rendered from the
    //    cached override so the row shows the layer's line style without opening the flyout. ──

    private MediaBrush _linePreviewBrush;
    private double _linePreviewThickness;
    private MediaDashes _linePreviewDashArray;

    /// <summary>Swatch stroke color — the override's color, or a neutral gray when the layer carries none.</summary>
    public MediaBrush LinePreviewBrush => _linePreviewBrush;
    /// <summary>Swatch stroke thickness (px) — a schematic map of line weight 1–16 (not exact millimeters).</summary>
    public double LinePreviewThickness => _linePreviewThickness;
    /// <summary>Swatch dash array in stroke-thickness units, or null for a solid line (Solid / no override).</summary>
    public MediaDashes LinePreviewDashArray => _linePreviewDashArray;

    private void RecomputePreview()
    {
        var ogs = _lineOverride;

        var rc = ogs?.ProjectionLineColor;
        _linePreviewBrush = new MediaSolidBrush(rc != null && rc.IsValid
            ? MediaColor.FromRgb(rc.Red, rc.Green, rc.Blue)
            : MediaColor.FromRgb(0x44, 0x44, 0x44));

        _linePreviewThickness = WeightToPx(ogs?.ProjectionLineWeight ?? -1);

        var pid = ogs?.ProjectionLinePatternId;
        _linePreviewDashArray = pid != null && _dashArrays.TryGetValue(pid, out var feet)
            ? BuildDashUnits(feet, _linePreviewThickness) : null;

        OnPropertyChanged(nameof(LinePreviewBrush));
        OnPropertyChanged(nameof(LinePreviewThickness));
        OnPropertyChanged(nameof(LinePreviewDashArray));
    }

    // Line weight 1..16 → a 1.0–4.0 px schematic thickness. No override (-1) reads as the thinnest.
    private static double WeightToPx(int weight)
    {
        if (weight < 1) return 1.0;
        int w = Math.Min(weight, 16);
        return 1.0 + (w - 1) / 15.0 * 3.0;
    }

    // Scale a pattern's on/off feet array to a WPF dash array (units of stroke thickness), normalized so the
    // whole repeat spans a fixed on-screen length — a few cycles fit the swatch regardless of the real scale.
    private static MediaDashes BuildDashUnits(double[] feet, double thickness)
    {
        if (feet == null || feet.Length < 2 || thickness <= 0) return null;
        double repeat = 0;
        foreach (var f in feet) repeat += f;
        if (repeat <= 1e-9) return null;

        const double targetRepeatPx = 22.0;
        double pxPerFt = targetRepeatPx / repeat;
        var dc = new MediaDashes();
        foreach (var f in feet) dc.Add(Math.Max(0.0, f * pxPerFt / thickness));
        return dc;
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
