#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Shim-side <see cref="IDmxModelSelection"/> for the cluster sub-builder (§8d): read the active
    /// document's current selection (the runs the designer picked on one wall) and highlight a cluster's
    /// runs back in the model (verify). Read-only — selection is not a model edit; invoked through the work
    /// queue so both calls run on the Revit API thread.
    /// </summary>
    public sealed class DmxModelSelection : IDmxModelSelection
    {
        private readonly UIDocument _uidoc;

        public DmxModelSelection(UIDocument uidoc) => _uidoc = uidoc;

        public IReadOnlyList<long> GetSelectedIds() =>
            _uidoc.Selection.GetElementIds().Select(id => id.ToRef().Value).ToList();

        public void Highlight(IReadOnlyList<long> ids)
        {
            var elementIds = ids.Select(v => new ElementRef(v).ToElementId()).ToList();
            _uidoc.Selection.SetElementIds(elementIds);
        }
    }
}
