#nullable disable
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Shim-side <see cref="ICircuitSelector"/> — selects + reveals a circuit in the active
    /// project. Replaces the old <c>SelectInProjectRequest</c> handler branch verbatim;
    /// invoked on the Revit API thread via the work queue.
    /// </summary>
    public class CircuitSelector : ICircuitSelector
    {
        private readonly UIDocument _uidoc;

        public CircuitSelector(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public bool SelectInProject(ElementRef circuitRef)
        {
            var id = circuitRef.ToElementId();
            var elem = _uidoc.Document.GetElement(id);
            if (elem == null) return false;

            _uidoc.Selection.SetElementIds(new[] { id });
            _uidoc.ShowElements(id);
            return true;
        }
    }
}
