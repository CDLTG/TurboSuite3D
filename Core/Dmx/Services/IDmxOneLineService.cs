#nullable enable
using System.Collections.Generic;
using TurboSuite.Dmx.OneLine;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Revit-free contract for drawing the per-loop one-line (BuildPlan Phase 4). Implemented shim-side
    /// against the active document; the Core ViewModel invokes it through the
    /// <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> so the view create + draw transaction run on
    /// the Revit API thread. Per loop: the program OWNS a Drafting View (deterministic name + the persisted
    /// view id), and a draw is a <b>wipe-and-redraw</b> of that view from the snapshot — never a hand-edit.
    /// </summary>
    public interface IDmxOneLineService
    {
        /// <param name="drawings">All loops' drawings off the last solve (passed whole, like placement);
        /// only <paramref name="onlyInterfaceNumber"/> is drawn this call.</param>
        /// <param name="systemName">The Control System label — seeds the owned view's deterministic name.</param>
        /// <param name="onlyInterfaceNumber">Draw ONLY the loop with this interface # (per-loop, like Place).</param>
        /// <param name="viewRegistry">Interface # → owned-view element id (the Revit-free long) from the
        /// persisted state, so a re-draw finds the same view by id even if the user renamed it.</param>
        DmxOneLineResult Draw(IReadOnlyList<DmxOneLineDrawing> drawings, string systemName,
                              int onlyInterfaceNumber, IReadOnlyDictionary<int, long> viewRegistry);

        /// <summary>Draw the single per-job wire legend into its own owned Drafting View (BuildPlan Phase 6) —
        /// same wipe-and-redraw ownership as the one-line, but one view per job (not per loop).</summary>
        /// <param name="drawing">The legend layout off the last solve's <see cref="DmxWireLegend"/>.</param>
        /// <param name="systemName">The Control System label — seeds the owned view's deterministic name.</param>
        /// <param name="existingViewId">The persisted legend view id (the Revit-free long), or 0 if never
        /// drawn, so a re-draw finds the same view by id even if the user renamed it.</param>
        DmxWireLegendResult DrawWireLegend(DmxWireLegendDrawing drawing, string systemName, long existingViewId);
    }
}
