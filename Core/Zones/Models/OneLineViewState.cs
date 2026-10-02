#nullable disable
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// The owned Drafting Views the control one-line + wire legend draw into, persisted across sessions so a
    /// redraw re-uses (wipes) the same views instead of leaving orphans. Mirrors the DMX one-line's
    /// <c>OneLineViews</c>/<c>WireLegendViewId</c> persistence, but lives in its OWN TurboZones ES schema
    /// (<c>ZonesOneLineViewStorageService</c>, a distinct GUID) so it never re-bumps the panel-settings schema
    /// that resets user settings on upgrade. Read shim-side at window open (passed into the Panel Breakdown VM
    /// like <c>savedSettings</c>); written on Draw via the work queue through <see cref="Services.IOneLineViewStore"/>.
    ///
    /// NOT a correctness dependency — redraws also find views by their deterministic name and prune stale sheets
    /// by name — so an empty state (fresh session, or a never-drawn job) simply means the first redraw re-finds
    /// everything by name. This persistence makes the id-keyed fast path survive a Revit restart.
    /// </summary>
    public sealed class OneLineViewState
    {
        /// <summary>1-based page index → owned one-line Drafting View element id (the Revit-free long).</summary>
        public Dictionary<int, long> PageViewIds { get; } = new Dictionary<int, long>();

        /// <summary>The per-job wire-legend owned view id (one per job, not per page); 0 ⇒ none drawn yet.</summary>
        public long WireLegendViewId { get; set; }
    }
}
