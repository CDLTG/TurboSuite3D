#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

/// <summary>
/// The one modeless TurboName window's ViewModel. It hosts the CAD Room Source config (<see cref="CadConfig"/>)
/// and the merged Generate-Regions surface (auto / rectangle / polygon), and drives every Revit write through
/// one shared <see cref="ExternalEvent"/> + <see cref="TurboNameApiHandler"/> (see CLAUDE.md "Modeless
/// pattern"). Exactly one request is in flight at a time — the shared event silently drops a second raise, so
/// user actions are gated on <c>_eventBusy</c>. Settings are dirty-tracked and saved once, on close.
/// </summary>
public class TurboNameViewModel : ViewModelBase
{
    private readonly ExternalEvent _event;
    private readonly TurboNameApiHandler _handler;

    /// <summary>CAD Room Source + region-layer configuration, edited inline in the TurboName window.</summary>
    public CadRoomSourceConfigViewModel CadConfig { get; }

    // ── Linked CAD Layers (folded-in VG → Imported Categories) ──
    /// <summary>Flat, file-then-layer sorted rows; the view groups them by <see cref="CadLayerRowViewModel.FileName"/>.</summary>
    public ObservableCollection<CadLayerRowViewModel> Layers { get; } = new();

    /// <summary>Grouped-by-file, Find-filtered projection of <see cref="Layers"/> bound by the window.</summary>
    public ICollectionView LayersView { get; }

    private string _layerFilterText = "";
    public string LayerFilterText
    {
        get => _layerFilterText;
        set { if (SetProperty(ref _layerFilterText, value)) LayersView.Refresh(); }
    }

    // ── Multi-select (mirrors native VG's bulk edits) ──
    // The layer list is a ListBox with SelectionMode=Extended; its SelectedItems can't be bound (same limitation
    // as DataGrid — see CLAUDE.md), so the view pushes the selection here on SelectionChanged. Every row edit
    // then asks TargetRows() whether it applies to one row or the whole selection.
    private readonly List<CadLayerRowViewModel> _selected = new();

    /// <summary>Called from the view's SelectionChanged. Replaces the tracked selection. Nothing in the window
    /// binds the selection itself — the ListBox's own highlight is the only feedback it needs.</summary>
    public void SetSelectedLayers(IEnumerable<CadLayerRowViewModel> rows)
    {
        _selected.Clear();
        if (rows != null) _selected.AddRange(rows);
    }

    // Which rows an edit on <paramref name="row"/> hits: the whole selection when the row is part of a
    // multi-selection (native VG behavior), otherwise just that row. Never returns an empty list.
    private List<CadLayerRowViewModel> TargetRows(CadLayerRowViewModel row)
    {
        if (_selected.Count > 1 && _selected.Contains(row)) return new List<CadLayerRowViewModel>(_selected);
        return new List<CadLayerRowViewModel> { row };
    }

    // Subcategories currently painted red (region-gen tagged) — drives the paint/unpaint diff and close revert.
    private readonly HashSet<ElementId> _painted = new();

    // ── Shared-event gate + close/save coordination ──
    private bool _eventBusy;       // a request is queued/running on the shared event
    private bool _closeAfterCurrent; // close was requested while a request was in flight
    private bool _saveThenClose;   // the on-close cleanup has been raised; the next close attempt may proceed

    /// <summary>True once any CAD setting changed — the config is saved once when the window closes.</summary>
    public bool SettingsDirty { get; private set; }

    // ── Merged Generate-Regions state ──
    private int _createdCount;
    private int _failedCount;
    private bool _isPicking;
    private string _statusText = "";
    private string _pickingHint = "";

    public int CreatedCount { get => _createdCount; set => SetProperty(ref _createdCount, value); }
    public int FailedCount { get => _failedCount; set => SetProperty(ref _failedCount, value); }
    public bool IsPicking
    {
        get => _isPicking;
        set { if (SetProperty(ref _isPicking, value)) OnPropertyChanged(nameof(LineEditingEnabled)); }
    }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string PickingHint { get => _pickingHint; set => SetProperty(ref _pickingHint, value); }

    private bool _isPreviewActive;
    /// <summary>True while the global red watershed preview is showing (off by default). While on, per-layer
    /// line editing is disabled — the red overlay and the line settings share one override slot.</summary>
    public bool IsPreviewActive
    {
        get => _isPreviewActive;
        private set { if (SetProperty(ref _isPreviewActive, value)) OnPropertyChanged(nameof(LineEditingEnabled)); }
    }

    /// <summary>Bound by the per-row line-edit (✎) buttons' IsEnabled. Off while the red preview is showing (they
    /// share the override slot) or a pick loop is running (the shared event is busy, so an apply would drop).</summary>
    public bool LineEditingEnabled => !_isPreviewActive && !_isPicking;

    private bool _isHidePicking;
    /// <summary>True while the "Hide by picking" loop owns the cursor — drives the button's lit state. The loop
    /// only ends on Escape (a running <c>PickObject</c> can't be cancelled from the window), so the button is
    /// disabled, not clickable-off, while it's on.</summary>
    public bool IsHidePicking { get => _isHidePicking; private set => SetProperty(ref _isHidePicking, value); }

    public ICommand RunAssignCommand { get; }
    public ICommand AutoGenerateCommand { get; }
    public ICommand RectangleCommand { get; }
    public ICommand PolygonCommand { get; }
    public ICommand TogglePreviewCommand { get; }
    public ICommand HideByPickCommand { get; }

    /// <summary>Raised to ask the window to actually close (after the on-close save has flushed).</summary>
    public event Action RequestClose;

    public TurboNameViewModel(CadRoomSourceSettings cadSettings, UIDocument uidoc, View view,
        ExternalEvent externalEvent, TurboNameApiHandler handler)
    {
        _event = externalEvent;
        _handler = handler;

        CadConfig = new CadRoomSourceConfigViewModel(cadSettings, uidoc);
        // Subscribe AFTER construction/LoadCadSettings so the initial load doesn't mark the config dirty.
        CadConfig.PropertyChanged += (_, __) => SettingsDirty = true;
        CadConfig.PickFromViewRequested += OnPickFromView;
        CadConfig.PickHeightBlockRequested += OnPickHeightBlock;

        BuildLayers(uidoc?.Document, view);
        SyncRolesFromConfig(); // seed each row's role toggles from the loaded settings
        LayersView = CollectionViewSource.GetDefaultView(Layers);
        LayersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CadLayerRowViewModel.FileName)));
        LayersView.Filter = LayerFilter;

        RunAssignCommand = new RelayCommand(OnAssign, () => !IsPicking);
        AutoGenerateCommand = new RelayCommand(OnAutoGenerate, () => !IsPicking);
        RectangleCommand = new RelayCommand(OnRectangle, () => !IsPicking);
        PolygonCommand = new RelayCommand(OnPolygon, () => !IsPicking);
        TogglePreviewCommand = new RelayCommand(TogglePreview, () => !IsPicking);
        HideByPickCommand = new RelayCommand(OnHideByPick, () => !IsPicking && Layers.Count > 0);
    }

    /// <summary>Pattern-dropdown roster for the Line Graphics flyout, read once in a valid API context.</summary>
    public IReadOnlyList<LinePatternOption> LinePatternOptions { get; private set; } = new List<LinePatternOption>();

    // Built once in the command's (valid API) context — enumerating subcategories + reading their overrides, the
    // line-pattern roster, and the per-pattern dash shapes are all reads, no DWG load.
    private void BuildLayers(Document doc, View view)
    {
        if (doc == null || view == null) return;
        LinePatternOptions = LayerLineGraphicsService.GetPatternOptions(doc);
        var dashArrays = LayerLineGraphicsService.GetPatternDashArrays(doc);
        foreach (var info in LinkedCadLayerService.Build(doc, view))
            Layers.Add(new CadLayerRowViewModel(
                info.FileName, info.LayerName, info.SubId, !info.Hidden, info.LineOverride, dashArrays,
                RequestToggleVisibility, OnRole));
    }

    /// <summary>Apply the Line Graphics flyout's composed override (persistent — never reverted on close). Hits
    /// every selected layer when the edited row is part of a multi-selection. On success each row caches the new
    /// override so reopening the flyout shows the current state.</summary>
    public void ApplyLineGraphics(CadLayerRowViewModel row, OverrideGraphicSettings overrides)
    {
        if (row == null || overrides == null) return;
        var targets = TargetRows(row);
        var subIds = targets.ConvertAll(r => r.SubId);
        if (RaiseUser(new ApplyLineGraphicsRequest { SubIds = subIds, Overrides = overrides }))
            foreach (var target in targets) target.LineOverride = overrides;
    }

    /// <summary>How many layers a Line Graphics edit started on <paramref name="row"/> would touch — the flyout
    /// titles itself with this so a bulk apply is never a surprise.</summary>
    public int LineGraphicsTargetCount(CadLayerRowViewModel row) => row == null ? 0 : TargetRows(row).Count;

    // Re-seed every row's role toggles from the config (the single source of truth). Silent — no callbacks.
    private void SyncRolesFromConfig()
    {
        foreach (var row in Layers)
            foreach (LayerRole role in Enum.GetValues(typeof(LayerRole)))
                row.SetRoleSilently(role, CadConfig.HasRole(role, row.FileName, row.LayerName));
    }

    // A row's role toggle changed: pure data — write it into the config and enforce single-select for Name/Ht.
    // Region-gen (W/D/A) tags no longer paint on toggle; the red is shown on demand by the Preview toggle.
    // W/D/A tags bulk across a multi-selection (tagging a dozen wall layers at once); Name/Ht deliberately do
    // NOT — they're single-value scopes, so a bulk tag would silently keep only the last row.
    private void OnRole(CadLayerRowViewModel row, LayerRole role, bool value)
    {
        CadConfig.SetRole(role, row.FileName, row.LayerName, value); // fires PropertyChanged → SettingsDirty

        if (role is LayerRole.Wall or LayerRole.Door or LayerRole.Area)
            foreach (var target in TargetRows(row))
            {
                if (ReferenceEquals(target, row)) continue;
                target.SetRoleSilently(role, value);
                CadConfig.SetRole(role, target.FileName, target.LayerName, value);
            }

        if (value && role is LayerRole.Name or LayerRole.Height)
            foreach (var other in Layers)
                if (!ReferenceEquals(other, row))
                    other.SetRoleSilently(role, false); // single-select across all rows

        // A retag while the preview is showing makes the red stale (snapshot-of-the-moment): auto-revert it so
        // the view never lies. The user re-presses Preview to check the new set.
        if (IsPreviewActive && role is LayerRole.Wall or LayerRole.Door or LayerRole.Area)
            RevertPreview("Preview turned off — W/D/A tags changed. Press Preview to re-check.");
    }

    // ── Global red watershed Preview toggle (off by default) ──

    // ON: paint every currently W/D/A-tagged layer red in one batched raise, auto-showing any hidden one (the
    // checkbox reflects it, same as before). OFF: revert them all. Snapshot-of-the-moment — retag then re-toggle
    // to refresh. While ON, per-layer line editing is disabled (they share the same override slot).
    private void TogglePreview()
    {
        if (IsPreviewActive)
        {
            RevertPreview(null);
            return;
        }

        var toPaint = new List<CadLayerRowViewModel>();
        foreach (var row in Layers)
            if (row.IsRegionGenTagged) toPaint.Add(row);
        if (toPaint.Count == 0)
        {
            StatusText = "Tag one or more layers W/D/A first — nothing to preview.";
            return;
        }

        var subIds = new List<ElementId>();
        var flipped = new List<CadLayerRowViewModel>();
        foreach (var row in toPaint)
        {
            if (!row.IsVisible) { row.SetVisibleSilently(true); flipped.Add(row); } // request un-hides too
            subIds.Add(row.SubId);
        }

        if (RaiseUser(new PaintRolePreviewsRequest { SubIds = subIds }))
        {
            foreach (var row in toPaint) _painted.Add(row.SubId);
            IsPreviewActive = true;
        }
        else
            foreach (var row in flipped) row.SetVisibleSilently(false); // event busy — undo optimistic show
    }

    // Turn the preview off (manual toggle-off or auto-off on a W/D/A change): revert every painted layer and
    // clear the active flag. No-op when already off. If the shared event is busy the red lingers until the next
    // action — a benign edge, since a role click rarely coincides with an in-flight request.
    private void RevertPreview(string status)
    {
        if (!IsPreviewActive) return;
        if (RaiseUser(new PaintRolePreviewsRequest { Revert = true }))
        {
            _painted.Clear();
            IsPreviewActive = false;
            if (status != null) StatusText = status;
        }
    }

    private bool LayerFilter(object item)
    {
        if (item is not CadLayerRowViewModel row) return false;
        if (string.IsNullOrWhiteSpace(_layerFilterText)) return true;
        return row.LayerName.IndexOf(_layerFilterText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Returns false when the shared event is busy (the row reverts its checkbox). The row's IsVisible has already
    // been flipped optimistically by the binding, so it IS the target state — the rest of a multi-selection is
    // driven to match it (silently: the request below covers them all in one transaction). On a busy event the
    // followers are rolled back here, the clicked row by its own setter.
    private bool RequestToggleVisibility(CadLayerRowViewModel row)
    {
        bool visible = row.IsVisible;
        var targets = TargetRows(row);
        var flipped = new List<CadLayerRowViewModel>();
        foreach (var target in targets)
        {
            if (ReferenceEquals(target, row) || target.IsVisible == visible) continue;
            target.SetVisibleSilently(visible);
            flipped.Add(target);
        }

        if (RaiseUser(new SetLayerVisibilityRequest
        {
            SubIds = targets.ConvertAll(r => r.SubId),
            Hidden = !visible
        })) return true;

        foreach (var target in flipped) target.SetVisibleSilently(!visible);
        return false;
    }

    // ── "Hide by picking" (native Import Instance ▸ Query ▸ "Hide in view") ──
    // One request owns the whole click-to-hide loop; each hit unchecks its row as the layer goes hidden. Exits on
    // Escape only, so the button lights up and disables rather than offering a toggle-off that couldn't work.
    private void OnHideByPick()
    {
        var hideable = new HashSet<ElementId>();
        foreach (var row in Layers) hideable.Add(row.SubId);
        if (hideable.Count == 0) return;

        IsPicking = true;
        IsHidePicking = true;
        PickingHint = "Click CAD geometry to hide its layer. Escape to finish.";

        var request = new HideLayerPickRequest { HideableSubIds = hideable };
        request.OnComplete = result =>
        {
            if (result is not LayerHiddenUpdate update) return;
            StatusText = update.Status;
            if (update.SubId == null) return;
            foreach (var row in Layers)
                if (update.SubId.Equals(row.SubId)) { row.SetVisibleSilently(false); break; }
        };
        request.OnFinished = () => { IsPicking = false; IsHidePicking = false; PickingHint = ""; };

        if (!RaiseUser(request))
        {
            IsPicking = false;
            IsHidePicking = false;
            PickingHint = "";
        }
    }

    private void OnPickFromView()
    {
        // After the pick stamps RoomNameLayer/link (or block scope), re-light the matching row's Name tag.
        RaiseUser(new PickLayerRequest { Pick = CadConfig.RunPick, OnFinished = SyncRolesFromConfig });
    }

    private void OnPickHeightBlock()
    {
        // Text-mode height block: sets its own block + tag pool; no row role to re-sync.
        RaiseUser(new PickLayerRequest { Pick = CadConfig.RunHeightBlockPick });
    }

    private void OnAssign()
    {
        RaiseUser(new AssignNamesRequest());
    }

    private void OnAutoGenerate()
    {
        IsPicking = true;
        PickingHint = "Generating regions from CAD room labels…";
        RaiseGenerate(new AutoGeneratePickRequest());
    }

    private void OnRectangle()
    {
        IsPicking = true;
        PickingHint = "Click two corners to draw a rectangle. Escape to pause.";
        RaiseGenerate(new RectanglePickRequest());
    }

    private void OnPolygon()
    {
        IsPicking = true;
        PickingHint = "Click corners to trace a room. Escape to close shape. Escape again to pause.";
        RaiseGenerate(new PolygonPickRequest());
    }

    private void RaiseGenerate(TurboNameRequest request)
    {
        request.OnComplete = result =>
        {
            if (result is PickLoopUpdate update)
            {
                CreatedCount = update.TotalCreated;
                FailedCount = update.TotalFailed;
                if (update.LastStatus != null)
                    StatusText = update.LastStatus;
            }
        };
        request.OnFinished = () => IsPicking = false;
        if (!RaiseUser(request))
            IsPicking = false; // event busy — undo the optimistic picking state
    }

    // ── Close flow: revert red previews + save once (if dirty), then let the window close ──

    /// <summary>Called from the window's Closing handler. Returns true to allow the close, false to cancel it
    /// (a cleanup pass — revert previews + save — is raised first; its completion re-requests the close).</summary>
    public bool TryClose()
    {
        if (_saveThenClose) return true;
        if (_eventBusy)
        {
            _closeAfterCurrent = true; // defer until the in-flight request finishes
            return false;
        }
        bool needsSave = SettingsDirty;
        bool needsRevert = _painted.Count > 0;
        if (!needsSave && !needsRevert) return true;

        _saveThenClose = true;
        RaiseInternal(new CloseCleanupRequest
        {
            Settings = needsSave ? CadConfig.ToModel() : null,
            RevertPreviews = needsRevert,
            OnFinished = () => { SettingsDirty = false; RequestClose?.Invoke(); }
        });
        return false;
    }

    // ── Shared-event plumbing ──

    /// <summary>Raise a user-initiated request. No-ops (returns false) if a request is already in flight — the
    /// single shared event would silently drop it.</summary>
    private bool RaiseUser(TurboNameRequest request)
    {
        if (_eventBusy) return false;
        RaiseInternal(request);
        return true;
    }

    private void RaiseInternal(TurboNameRequest request)
    {
        var userFinished = request.OnFinished;
        request.OnFinished = () =>
        {
            userFinished?.Invoke();
            _eventBusy = false;
            if (_closeAfterCurrent)
            {
                _closeAfterCurrent = false;
                RequestClose?.Invoke(); // re-attempt close now that the event is free
            }
        };
        _handler.CurrentRequest = request;
        _eventBusy = true;
        _event.Raise();
    }
}
