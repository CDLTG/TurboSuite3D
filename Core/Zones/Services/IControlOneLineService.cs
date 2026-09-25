#nullable enable
using System.Collections.Generic;
using TurboSuite.Zones.OneLine;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Revit-free contract for drawing the control one-line. Implemented shim-side against the active
    /// document; the ViewModel invokes it through <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> so the
    /// view create + draw transaction run on the Revit API thread. Each 42×30 page OWNS a Drafting View
    /// (deterministic name + persisted view id, keyed by page index), and a draw is a <b>wipe-and-redraw</b>
    /// of that view from the <see cref="ControlOneLineDrawing"/> snapshot — never a hand-edit. Mirrors
    /// <c>IDmxOneLineService</c>, but keyed by page index (stable) rather than an interface number.
    /// </summary>
    public interface IControlOneLineService
    {
        /// <param name="pages">Every page of the one-line off the last solve (stage 1: exactly one).</param>
        /// <param name="systemName">The control-system label — seeds each owned view's deterministic name.</param>
        /// <param name="viewRegistry">Page index → owned-view element id (the Revit-free long) from persisted
        /// state, so a re-draw finds the same view by id even if the user renamed it.</param>
        /// <returns>One result per page drawn, carrying the (created or re-used) view id to persist.</returns>
        IReadOnlyList<ControlOneLineResult> Draw(IReadOnlyList<ControlOneLineDrawing> pages, string systemName,
            IReadOnlyDictionary<int, long> viewRegistry);
    }
}
