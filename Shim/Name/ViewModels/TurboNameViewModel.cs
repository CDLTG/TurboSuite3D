#nullable disable
using System;
using System.Windows.Input;
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

    // ── Shared-event gate + close/save coordination ──
    private bool _eventBusy;       // a request is queued/running on the shared event
    private bool _closeAfterCurrent; // close was requested while a request was in flight
    private bool _saveThenClose;   // the on-close save has been raised; the next close attempt may proceed

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

    public TurboNameViewModel(CadRoomSourceSettings cadSettings, UIDocument uidoc,
        ExternalEvent externalEvent, TurboNameApiHandler handler)
    {
        _event = externalEvent;
        _handler = handler;

        CadConfig = new CadRoomSourceConfigViewModel(cadSettings, uidoc);
        // Subscribe AFTER construction/LoadCadSettings so the initial load doesn't mark the config dirty.
        CadConfig.PropertyChanged += (_, __) => SettingsDirty = true;
        CadConfig.PickFromViewRequested += OnPickFromView;

        RunAssignCommand = new RelayCommand(OnAssign, () => !IsPicking);
        AutoGenerateCommand = new RelayCommand(OnAutoGenerate, () => !IsPicking);
        RectangleCommand = new RelayCommand(OnRectangle, () => !IsPicking);
        PolygonCommand = new RelayCommand(OnPolygon, () => !IsPicking);
    }

    private void OnPickFromView()
    {
        RaiseUser(new PickLayerRequest { Pick = CadConfig.RunPick });
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

    // ── Close flow: save once (if dirty), then let the window close ──

    /// <summary>Called from the window's Closing handler. Returns true to allow the close, false to cancel it
    /// (a save is raised first; its completion re-requests the close, which then passes).</summary>
    public bool TryClose()
    {
        if (!SettingsDirty || _saveThenClose) return true;
        if (_eventBusy)
        {
            _closeAfterCurrent = true; // defer until the in-flight request finishes
            return false;
        }
        _saveThenClose = true;
        RaiseInternal(new SaveSettingsRequest
        {
            Settings = CadConfig.ToModel(),
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
