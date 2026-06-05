#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Shim-side <see cref="ILoadNameWriter"/> — binds <see cref="LoadNameService"/> to the
    /// active document. Invoked on the Revit API thread via the work queue.
    /// </summary>
    public class LoadNameWriter : ILoadNameWriter
    {
        private readonly Document _doc;
        private readonly LoadNameService _service;

        public LoadNameWriter(Document doc, LoadNameService service)
        {
            _doc = doc;
            _service = service;
        }

        public int UpdateLoadNames(IReadOnlyList<ZonesCircuitData> circuits)
            => _service.UpdateLoadNames(_doc, circuits.ToList());
    }
}
