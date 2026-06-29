#nullable enable
using System.Collections.Generic;
using TurboSuite.Dmx.Placement;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Revit-free contract for placing a solved system's decoders + drivers (BuildPlan Phase 2). Implemented
    /// shim-side against the active <c>UIDocument</c>; the Core ViewModel invokes it through the
    /// <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> so the click-to-place picks + transaction run on
    /// the Revit API thread. Loop-by-loop: the shim prompts for one point per loop, drops that loop's
    /// devices, writes the decoder <c>Switch ID</c>, and tags decoder (Switch ID) + driver (Type Mark).
    /// </summary>
    public interface IDmxPlacementService
    {
        /// <param name="plan">The full solved system — passed whole so DEC #s + orphan validity stay
        /// system-wide even when only one loop is being placed.</param>
        /// <param name="locked">Lock state (§8c) — gates orphan removal: auto when Unlocked, confirmed when Locked.</param>
        /// <param name="registry">The placement registry (DEC # → decoder/driver ids) from the persisted state,
        /// so an orphaned decoder AND its paired driver can be removed exactly (Option-A cleanup).</param>
        /// <param name="onlyInterfaceNumber">When set, pick + place ONLY the loop with this interface # (the
        /// loop-centric per-loop Place); orphan cleanup still reconciles against the whole plan. Null ⇒ place
        /// every loop (place-all).</param>
        DmxPlacementResult Place(DmxPlacementPlan plan, bool locked, IReadOnlyList<DmxPlacedPair> registry,
                                 int? onlyInterfaceNumber = null);
    }
}
