#nullable enable
using System.Collections.Generic;
using TurboSuite.Dali.Overlay;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Revit-free contract for the DALI Control-Zone color overlay — a copy of <c>IDmxZoneColorService</c>.
    /// A view aid, not a deliverable: while the TurboDALI window is open the active view's DALI fixtures are
    /// colored by their <c>Control Zone</c> so the designer can tell zones apart as they pool and group them;
    /// on close it reverts. Implemented shim-side against the ACTIVE view via direct per-element graphic
    /// overrides; the ViewModel invokes it through the work queue so the override transaction runs on the
    /// Revit API thread.
    ///
    /// <see cref="Apply"/> colors every DALI fixture whose <c>Control Zone</c> has a palette entry;
    /// <see cref="Revert"/> clears exactly the overrides it applied, restoring the view (the user's own
    /// per-element overrides are left alone). The shim remembers what it colored, so a re-Apply or Revert is
    /// precise.
    /// </summary>
    public interface IDaliZoneColorService
    {
        /// <summary>Color the active view's DALI fixtures by Control Zone. Returns a short status (e.g. a
        /// view-type-lockout notice), or an empty string when applied cleanly / nothing to color.</summary>
        string Apply(IReadOnlyDictionary<string, DaliColor> zoneColors);

        /// <summary>Clear the overrides we applied to the views we colored, restoring them.</summary>
        string Revert();
    }
}
