#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="IRoomOrderStore"/> — binds the Revit-free contract to the
    /// active document. The project-wide room order persists via the shared
    /// <see cref="TurboSuite.Shared.Services.RoomOrderStorageService"/>; the keypad
    /// room-sort toggle persists via the TurboNumber-local
    /// <see cref="CircuitNamingStorageService"/>. Must be invoked on the Revit API thread
    /// (via <see cref="RevitWorkQueue"/>).
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

        public void SaveKeypadRoomSorted(bool isSorted)
            => CircuitNamingStorageService.SaveKeypadRoomSorted(_doc, isSorted);
    }
}
