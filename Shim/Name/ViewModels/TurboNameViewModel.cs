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

    public ICommand RunAssignCommand { get; }
    public ICommand AutoGenerateCommand { get; }
    public ICommand RectangleCommand { get; }
    public ICommand PolygonCommand { get; }
    public ICommand TogglePreviewCommand { get; }

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

    /// <summary>Apply the Line Graphics flyout's composed override to a layer (persistent — never reverted on
    /// close). On success the row caches the new override so reopening the flyout shows the current state.</summary>
    public void ApplyLineGraphics(CadLayerRowViewModel row, OverrideGraphicSettings overrides)
    {
        if (row == null || overrides == null) return;
        if (RaiseUser(new ApplyLineGraphicsRequest { SubId = row.SubId, Overrides = overrides }))
            row.LineOverride = overrides;
    }

    // Re-seed every row's role toggles from the config (the single source of truth). Silent — no callbacks.
    private void SyncRolesFromConfig()
    {
        foreach (var row in Layers)
            foreach (LayerRole role in Enum.GetValues(typeof(LayerRole)))
                row.SetRoleSilently(role, CadConfig.HasRole(role, row.FileName, row.LayerName));
    }

    // A row's role toggle changed: pure data — write it into the config and enforce single-select for Name/Ht.
    // Region-gen (W/D/A) tags no longer paint on toggle; the red is shown on demand by the Preview toggle.
    private void OnRole(CadLayerRowViewModel row, LayerRole role, bool value)
    {
        CadConfig.SetRole(role, row.FileName, row.LayerName, value); // fires PropertyChanged → SettingsDirty

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

    // Returns false when the shared event is busy (the row reverts its checkbox). Hidden = the target state.
    private bool RequestToggleVisibility(CadLayerRowViewModel row)
    {
        return RaiseUser(new SetLayerVisibilityRequest { SubId = row.SubId, Hidden = !row.IsVisible });
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
