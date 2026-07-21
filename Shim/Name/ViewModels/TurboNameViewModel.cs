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
    public bool IsPicking { get => _isPicking; set => SetProperty(ref _isPicking, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string PickingHint { get => _pickingHint; set => SetProperty(ref _pickingHint, value); }

    public ICommand RunAssignCommand { get; }
    public ICommand AutoGenerateCommand { get; }
    public ICommand RectangleCommand { get; }
    public ICommand PolygonCommand { get; }

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
    }

    // Built once in the command's (valid API) context — enumerating subcategories is a read, no DWG load.
    private void BuildLayers(Document doc, View view)
    {
        if (doc == null || view == null) return;
        foreach (var info in LinkedCadLayerService.Build(doc, view))
            Layers.Add(new CadLayerRowViewModel(
                info.FileName, info.LayerName, info.SubId, !info.Hidden, RequestToggleVisibility, OnRole));
    }

    // Re-seed every row's role toggles from the config (the single source of truth). Silent — no callbacks.
    private void SyncRolesFromConfig()
    {
        foreach (var row in Layers)
            foreach (LayerRole role in Enum.GetValues(typeof(LayerRole)))
                row.SetRoleSilently(role, CadConfig.HasRole(role, row.FileName, row.LayerName));
    }

    // A row's role toggle changed: write it into the config, enforce single-select for Name/Ht, and diff the
    // red preview for region-gen roles.
    private void OnRole(CadLayerRowViewModel row, LayerRole role, bool value)
    {
        CadConfig.SetRole(role, row.FileName, row.LayerName, value); // fires PropertyChanged → SettingsDirty

        if (value && role is LayerRole.Name or LayerRole.Height)
            foreach (var other in Layers)
                if (!ReferenceEquals(other, row))
                    other.SetRoleSilently(role, false); // single-select across all rows

        if (role is LayerRole.Wall or LayerRole.Door or LayerRole.Area)
            UpdatePreview(row);
    }

    // Paint a row red when it first gains a region-gen role; un-paint when it loses the last one.
    private void UpdatePreview(CadLayerRowViewModel row)
    {
        bool nowTagged = row.IsRegionGenTagged;
        bool wasPainted = _painted.Contains(row.SubId);

        if (nowTagged && !wasPainted)
        {
            bool ensureVisible = !row.IsVisible;
            if (ensureVisible) row.SetVisibleSilently(true); // reflect the auto-show; the request un-hides too
            if (RaiseUser(new SetLayerRolePreviewRequest { SubId = row.SubId, Painted = true, EnsureVisible = ensureVisible }))
                _painted.Add(row.SubId);
            else if (ensureVisible)
                row.SetVisibleSilently(false); // event busy — undo the optimistic checkbox flip
        }
        else if (!nowTagged && wasPainted)
        {
            if (RaiseUser(new SetLayerRolePreviewRequest { SubId = row.SubId, Painted = false }))
                _painted.Remove(row.SubId);
        }
    }

    // Paint-on-load (TurboName-10): when the window opens against a job whose W/D/A tags were saved last
    // session, SyncRolesFromConfig re-lights the toggles silently — but the transient red preview is off, so the
    // toggles and the view drift apart. Re-establish the red for every tagged layer in ONE batched raise (a
    // per-row loop would drop every raise after the first past the shared-event gate). Called once, on window
    // load. After this, the live per-toggle paint path in UpdatePreview keeps them in sync.
    public void PaintTaggedPreviewsOnLoad()
    {
        var toPaint = new List<CadLayerRowViewModel>();
        foreach (var row in Layers)
            if (row.IsRegionGenTagged && !_painted.Contains(row.SubId))
                toPaint.Add(row);
        if (toPaint.Count == 0) return;

        var subIds = new List<ElementId>();
        var flipped = new List<CadLayerRowViewModel>();
        foreach (var row in toPaint)
        {
            if (!row.IsVisible) { row.SetVisibleSilently(true); flipped.Add(row); } // request un-hides too
            subIds.Add(row.SubId);
        }

        if (RaiseUser(new PaintRolePreviewsRequest { SubIds = subIds }))
            foreach (var row in toPaint) _painted.Add(row.SubId);
        else
            foreach (var row in flipped) row.SetVisibleSilently(false); // event busy — undo optimistic show
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
