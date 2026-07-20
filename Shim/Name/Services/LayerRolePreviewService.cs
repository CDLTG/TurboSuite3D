#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Name.Services;

/// <summary>
/// Transient red "watershed preview" overlay for region-gen-tagged CAD layers. When a layer is tagged W/D/A,
/// its subcategory is painted red via <see cref="View.SetCategoryOverrides"/> so the user sees exactly which
/// geometry the watershed will consume; untagging (or window close) restores the layer's <b>exact prior</b>
/// category override — not a blank one. Mirrors <c>DmxZoneColorService</c>'s snapshot/apply/revert lifecycle,
/// but at the category (subcategory) level rather than per element.
///
/// The spike proved imported-DWG subcategories accept <c>SetCategoryOverrides</c> and that the effect is
/// visible; a red override on a VG-hidden layer wouldn't show, so the caller un-hides first (the tag also
/// flips the row's visibility checkbox). Caller owns the transaction.
/// </summary>
public sealed class LayerRolePreviewService
{
    // (view, subcategory) → the category override that was in place before we painted it red. Restored verbatim.
    private readonly Dictionary<(ElementId View, ElementId Sub), OverrideGraphicSettings> _snapshots = new();

    public bool HasActive => _snapshots.Count > 0;

    /// <summary>Paint one layer red, snapshotting its prior override once (idempotent per view+sub).</summary>
    public void Paint(View view, ElementId subId)
    {
        if (view == null || subId == null) return;
        var key = (view.Id, subId);
        if (!_snapshots.ContainsKey(key))
            _snapshots[key] = view.GetCategoryOverrides(subId);
        view.SetCategoryOverrides(subId, MakeRed());
    }

    /// <summary>Restore one layer's exact prior override and forget it. No-op if we never painted it.</summary>
    public void Unpaint(View view, ElementId subId)
    {
        if (view == null || subId == null) return;
        var key = (view.Id, subId);
        if (_snapshots.TryGetValue(key, out var prior))
        {
            view.SetCategoryOverrides(subId, prior);
            _snapshots.Remove(key);
        }
    }

    /// <summary>Restore every painted layer (on window close). Skipped by the doc-close guard's forceClose.</summary>
    public void RevertAll(Document doc)
    {
        if (doc == null) return;
        foreach (var kv in _snapshots)
        {
            if (doc.GetElement(kv.Key.View) is View view)
                view.SetCategoryOverrides(kv.Key.Sub, kv.Value);
        }
        _snapshots.Clear();
    }

    private static OverrideGraphicSettings MakeRed()
    {
        var red = new Color(220, 30, 30);
        var ogs = new OverrideGraphicSettings();
        ogs.SetProjectionLineColor(red);
        ogs.SetProjectionLineWeight(6);
        ogs.SetSurfaceForegroundPatternVisible(true);
        ogs.SetSurfaceForegroundPatternColor(red);
        return ogs;
    }
}
