#nullable disable
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="IDeviceSelector"/> — selects and reveals a device in the
    /// active project. Invoked on the Revit API thread via <see cref="RevitWorkQueue"/>;
    /// returns false when the element no longer exists.
    /// </summary>
    public class DeviceSelector : IDeviceSelector
    {
        private readonly UIDocument _uidoc;

        public DeviceSelector(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public bool SelectInProject(ElementRef elementRef)
        {
            var id = elementRef.ToElementId();
            var elem = _uidoc.Document.GetElement(id);
            if (elem == null) return false;

            _uidoc.Selection.SetElementIds(new[] { id });
            _uidoc.ShowElements(id);
            return true;
        }
    }
}
