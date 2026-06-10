#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Revit-free contract for the TurboRPS modeless dashboard. Implemented shim-side
    /// (binding to the active <c>Document</c>/<c>UIDocument</c>) and invoked by the Core
    /// ViewModel inside an <see cref="IRevitWorkQueue"/> work item, so every Revit
    /// transaction/selection runs on the Revit API thread. Mirrors
    /// <c>IZonesRevitOperations</c>.
    /// </summary>
    public interface IRpsRevitOperations
    {
        /// <summary>Selects + reveals a circuit's member elements (fixtures + supplies) in the
        /// active view. Returns false if the circuit no longer exists.</summary>
        bool SelectInProject(ElementRef circuitRef);

        /// <summary>Swaps each device to its target type IN PLACE, all in ONE transaction
        /// (one undo step). Returns the count actually swapped. Preserves
        /// location/workset/plan-visibility param/wiring/tags and the manual switch-system
        /// memberships (never deletes).</summary>
        int SwapDriverTypes(IReadOnlyList<DriverSwap> swaps);

        /// <summary>Re-collects + reclassifies all RPS circuits on the Revit thread (for Rescan).</summary>
        IReadOnlyList<RpsCircuitData> Rescan();
    }
}
