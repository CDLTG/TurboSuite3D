#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public abstract class RevitApiRequest
    {
        public Action<object> OnComplete { get; set; }
    }

    public class UpdateLoadNamesRequest : RevitApiRequest
    {
        public List<ZonesCircuitData> Circuits { get; set; }
        // Result: int (count updated)
    }

    public class SavePanelSettingsRequest : RevitApiRequest
    {
        public PanelSettings Settings { get; set; }
        // Result: true on success
    }

    public class SelectInProjectRequest : RevitApiRequest
    {
        public ElementId CircuitId { get; set; }
        // Result: true if element existed and was selected
    }
}
