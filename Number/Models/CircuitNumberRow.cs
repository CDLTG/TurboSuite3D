#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Number.Models
{
    public class CircuitNumberRow
    {
        public ElementId ElementId { get; set; }
        public string CircuitNumber { get; set; }
        public string Panel { get; set; }
        public string LoadName { get; set; }
    }
}
