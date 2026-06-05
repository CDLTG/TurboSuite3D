#nullable disable
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Shim-side <see cref="IPanelSettingsStore"/> — binds
    /// <see cref="ZonesPanelSettingsStorageService"/> to the active document. Invoked on the
    /// Revit API thread via the work queue (it opens its own transaction).
    /// </summary>
    public class PanelSettingsStore : IPanelSettingsStore
    {
        private readonly Document _doc;

        public PanelSettingsStore(Document doc)
        {
            _doc = doc;
        }

        public void Save(PanelSettings settings)
            => ZonesPanelSettingsStorageService.Save(_doc, settings);
    }
}
