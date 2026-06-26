#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Revit-free contract for the cluster sub-builder's two model touches (§8d): read what the designer has
    /// selected in the drawing (to turn a wall's worth of runs into a cluster), and highlight a cluster's
    /// runs back in the model (verify). Implemented shim-side over <c>UIDocument.Selection</c>; the Core
    /// ViewModel invokes it through the <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> so both run on
    /// the Revit API thread. Read-only — selection isn't a model edit.
    /// </summary>
    public interface IDmxModelSelection
    {
        /// <summary>The element ids currently selected in the active document.</summary>
        IReadOnlyList<long> GetSelectedIds();

        /// <summary>Select (highlight) the given elements in the active document.</summary>
        void Highlight(IReadOnlyList<long> ids);
    }
}
