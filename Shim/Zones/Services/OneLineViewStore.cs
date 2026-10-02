#nullable disable
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Shim-side <see cref="IOneLineViewStore"/> — binds <see cref="ZonesOneLineViewStorageService"/> to the
    /// active document. Invoked on the Revit API thread via the work queue (it opens its own transaction).
    /// </summary>
    public class OneLineViewStore : IOneLineViewStore
    {
        private readonly Document _doc;

        public OneLineViewStore(Document doc)
        {
            _doc = doc;
        }

        public void Save(OneLineViewState state)
            => ZonesOneLineViewStorageService.Save(_doc, state);
    }
}
