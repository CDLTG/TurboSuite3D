#nullable enable
using System.Collections.Generic;
using TurboSuite.Dmx.Overlay;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Revit-free contract for the Phase 5 Control-Zone color overlay (BuildPlan Phase 5) — a view aid, not
    /// a deliverable. Implemented shim-side against the ACTIVE view via direct per-element graphic overrides
    /// (the UI's "Override Graphics in View ▸ By Element"); the Core ViewModel invokes it through the work
    /// queue so the override transaction runs on the Revit API thread. (Named <c>ParameterFilterElement</c>s
    /// were the original plan but the firm's <c>Control Zone</c> shared param won't drive a filter rule and
    /// templates blocked that path — see the shim for the full rationale.)
    ///
    /// <see cref="Apply"/> colors every fixture whose <c>Control Zone</c> has a palette entry; <see cref="Revert"/>
    /// clears exactly the overrides we applied, restoring the view (the user's own per-element overrides are
    /// left alone). The shim remembers what it colored, so a re-Apply or Revert is precise.
    /// </summary>
    public interface IDmxZoneColorService
    {
        /// <summary>Color the active view's DMX fixtures by Control Zone. Returns a short status (e.g. a
        /// view-template-lockout notice), or an empty string when applied cleanly / nothing to color.</summary>
        string Apply(IReadOnlyDictionary<string, DmxColor> zoneColors);

        /// <summary>Remove our filters from the active view and delete the filter elements.</summary>
        string Revert();
    }
}
