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

/// <summary>
/// Window-close cleanup in one API pass: revert the transient red role previews (if any) and persist the CAD
/// Room Source settings (if dirty). Raised once from the ViewModel's close flow; its completion closes the
/// window. The doc-close guard's forceClose bypasses this entirely (never touches a closing document).
/// </summary>
public class CloseCleanupRequest : TurboNameRequest
{
    /// <summary>Non-null ⇒ save these settings. Null ⇒ nothing dirty, skip the save.</summary>
    public Shared.Models.CadRoomSourceSettings Settings { get; set; }

    /// <summary>True ⇒ revert every painted red role preview to its snapshotted prior override.</summary>
    public bool RevertPreviews { get; set; }
}

/// <summary>
/// Show/hide one linked-CAD layer (subcategory) in the locked view — the folded-in VG → Imported Categories
/// checkbox. The hide persists (that's the point), so no revert on close.
/// </summary>
public class SetLayerVisibilityRequest : TurboNameRequest
{
    public Autodesk.Revit.DB.ElementId SubId { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// Drives the global red watershed Preview toggle in one API pass. Toggle ON (<see cref="Revert"/> = false):
/// snapshot each flagged layer's current override slot — preserving its persistent line settings — un-hide any
/// hidden one so the red shows, and paint them all red. Toggle OFF (<see cref="Revert"/> = true): restore every
/// snapshotted slot verbatim (<see cref="LayerRolePreviewService.RevertAll"/>), composing the base line
/// settings back. Batching the whole set in one raise obeys the shared-event single-raise rule (a per-row loop
/// would drop every raise after the first). Red is transient — never persisted — so a toggle left ON at close is
/// reverted by the close cleanup. See <see cref="LayerRolePreviewService"/>.
/// </summary>
public class PaintRolePreviewsRequest : TurboNameRequest
{
    /// <summary>Flagged subcategories to paint red. Ignored when <see cref="Revert"/> is true.</summary>
    public System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> SubIds { get; set; }

    /// <summary>True ⇒ toggle OFF: revert every painted layer instead of painting.</summary>
    public bool Revert { get; set; }
}

/// <summary>
/// Apply the per-layer VG → Imported Categories *Lines* override (color / weight / pattern) built by the Line
/// Graphics flyout (TurboName-12). The <see cref="Overrides"/> object is composed on the WPF thread (a pure
/// value object — clearing a field = <c>SetProjectionLineWeight(-1)</c> / <c>SetProjectionLineColor(Color
/// .InvalidColorValue)</c> / <c>SetProjectionLinePatternId(ElementId.InvalidElementId)</c>, all spike-confirmed)
/// off a clone of the layer's current override, so surface/halftone overrides are preserved. Persists on the
/// view like the visibility checkbox — never reverted on close (unlike the transient red preview).
/// </summary>
public class ApplyLineGraphicsRequest : TurboNameRequest
{
    public Autodesk.Revit.DB.ElementId SubId { get; set; }
    public Autodesk.Revit.DB.OverrideGraphicSettings Overrides { get; set; }
}

/// <summary>Status update sent from a pick/generate handler to the ViewModel during/after the loop.</summary>
public record PickLoopUpdate(int TotalCreated, int TotalFailed, bool LoopEnded, string LastStatus = null);
