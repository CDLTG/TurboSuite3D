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
/// Show/hide linked-CAD layers (subcategories) in the locked view — the folded-in VG → Imported Categories
/// checkbox. Carries a LIST because the layer table is multi-select: checking one row of a selection applies to
/// the whole selection, and the shared event drops every raise after the first, so a per-row loop would lose all
/// but one. The hide persists (that's the point), so no revert on close.
/// </summary>
public class SetLayerVisibilityRequest : TurboNameRequest
{
    public System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> SubIds { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// "Hide by picking" — the native Import Instance ▸ Query ▸ "Hide in view" workflow, as a loop: click linked-CAD
/// geometry, its LAYER goes hidden in the view, repeat until Escape. Same shape as the region pick loops (one
/// request owns the whole loop; each hide is its own transaction + refresh, so the layer vanishes under the
/// cursor), and it writes the exact same view slot as the row checkbox — so the row just unchecks.
///
/// <see cref="HideableSubIds"/> is the guard: a pick is only honored when the geometry's GraphicsStyle resolves
/// to a subcategory that's actually a listed layer row. Spike-confirmed on 2D SETUP TEST (5 picks: polyline,
/// arc, text, hatch face, and block-internal geometry on layer "0") — every one resolved to a layer subcategory
/// whose <c>.Parent</c> is the import's category, never to the parent itself. The guard still refuses anything
/// unrecognized, because resolving to the parent would blank the entire DWG rather than one layer.
/// </summary>
public class HideLayerPickRequest : TurboNameRequest
{
    /// <summary>The layer subcategories the table knows about — the only ids the loop will hide.</summary>
    public System.Collections.Generic.HashSet<Autodesk.Revit.DB.ElementId> HideableSubIds { get; set; }
}

/// <summary>One layer hidden by the pick loop: the row to uncheck, plus a status line for the window.</summary>
public record LayerHiddenUpdate(Autodesk.Revit.DB.ElementId SubId, string Status);

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
///
/// Carries a LIST for the multi-select table: editing one row of a selection stamps the same composed override
/// on every selected layer (what native VG multi-select does). The clone is of the CLICKED row's override, so a
/// bulk apply also normalizes the others' surface/halftone bits to that row's — accepted, and the reason the
/// flyout titles itself "N layers" when it's about to do that.
/// </summary>
public class ApplyLineGraphicsRequest : TurboNameRequest
{
    public System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> SubIds { get; set; }
    public Autodesk.Revit.DB.OverrideGraphicSettings Overrides { get; set; }
}

/// <summary>Status update sent from a pick/generate handler to the ViewModel during/after the loop.</summary>
public record PickLoopUpdate(int TotalCreated, int TotalFailed, bool LoopEnded, string LastStatus = null);
