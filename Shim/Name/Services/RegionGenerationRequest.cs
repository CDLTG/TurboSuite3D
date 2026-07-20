#nullable disable
using System;

namespace TurboSuite.Name.Services;

/// <summary>
/// Base for every unit of Revit work the modeless TurboName window queues onto its single shared
/// <see cref="Autodesk.Revit.UI.ExternalEvent"/> (see CLAUDE.md "Modeless pattern"). The handler
/// (<see cref="TurboNameApiHandler"/>) runs exactly one request per raise.
/// </summary>
public abstract class TurboNameRequest
{
    /// <summary>Incremental UI update sent from the handler during the request (e.g. a
    /// <see cref="PickLoopUpdate"/> per region created). May fire zero or more times.</summary>
    public Action<object> OnComplete { get; set; }

    /// <summary>Terminal signal: the handler calls this exactly once when the request has fully finished
    /// (after any interactive loop ends), so the ViewModel can free the shared-event gate and flush a
    /// pending save. Always dispatched to the WPF thread.</summary>
    public Action OnFinished { get; set; }
}

/// <summary>Rectangle mode: two-click pick loop.</summary>
public class RectanglePickRequest : TurboNameRequest { }

/// <summary>Polygon mode: multi-click pick loop, Escape closes current polygon.</summary>
public class PolygonPickRequest : TurboNameRequest { }

/// <summary>
/// Auto-generate mode: one-shot watershed partition of the whole floor from CAD room labels.
/// Runs the pipeline once and reports diagnostics (leaks / collision px / doors sealed) — no pick loop.
/// </summary>
public class AutoGeneratePickRequest : TurboNameRequest { }

/// <summary>
/// Assign Room Names: reads the linked CAD, extracts room names/heights, assigns them to Room Region
/// filled regions and places TextNotes — all inside the handler's API context (moved out of the command's
/// synchronous <c>Execute</c> when TurboName went modeless).
/// </summary>
public class AssignNamesRequest : TurboNameRequest { }

/// <summary>
/// Pick-from-view: runs the CAD-layer pick in a valid API context. The pick + classification logic lives on
/// <see cref="ViewModels.CadRoomSourceConfigViewModel.RunPick"/> (it mutates that VM's fields) and is carried
/// here as <see cref="Pick"/>; the handler just invokes it on the Revit thread.
/// </summary>
public class PickLayerRequest : TurboNameRequest
{
    public Action Pick { get; set; }
}

/// <summary>Persist the current CAD Room Source settings (own transaction) — coalesced by the ViewModel.</summary>
public class SaveSettingsRequest : TurboNameRequest
{
    public Shared.Models.CadRoomSourceSettings Settings { get; set; }
}

/// <summary>Status update sent from a pick/generate handler to the ViewModel during/after the loop.</summary>
public record PickLoopUpdate(int TotalCreated, int TotalFailed, bool LoopEnded, string LastStatus = null);
