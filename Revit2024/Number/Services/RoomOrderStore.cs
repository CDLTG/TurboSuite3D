#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="IRoomOrderStore"/> — binds the Revit-free contract to the
    /// active document and the static <see cref="RoomOrderStorageService"/>
    /// ExtensibleStorage helper. Must be invoked on the Revit API thread (via
    /// <see cref="RevitWorkQueue"/>).
    /// </summary>
    public class RoomOrderStore : IRoomOrderStore
    {
        private readonly Document _doc;

        public RoomOrderStore(Document doc)
        {
            _doc = doc;
        }

        public void SaveRoomOrder(IReadOnlyList<(string Name, int ClickOrder)> roomOrder)
            => RoomOrderStorageService.Save(_doc, roomOrder.ToList());

        public void SaveSidebarVisible(bool isVisible)
            => RoomOrderStorageService.SaveSidebarVisible(_doc, isVisible);
    }
}
