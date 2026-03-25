#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Zones.Models
{
    public enum LabelSource
    {
        CircuitComments,
        FixtureComments,
        Fallback,
        None
    }

    public class ZonesCircuitData
    {
        public ElementId CircuitId { get; set; }
        public string CircuitNumber { get; set; }
        public string DimmingType { get; set; }
        public string RoomName { get; set; }
        public string CurrentLoadName { get; set; }
        public string CircuitComments { get; set; }
        public string FixtureComments { get; set; }
        public string LoadClassificationName { get; set; }
        public string PanelName { get; set; }
        public string RoomOverride { get; set; }
        public ElementId RegionId { get; set; }
        public string UpdatedLoadName { get; set; }
        public LabelSource LabelSource { get; set; }
        public bool IsWiredToSwitch { get; set; }
    }
}
