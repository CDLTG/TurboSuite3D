#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Zones.Models
{
    public class CircuitMove
    {
        public ElementId CircuitId { get; set; }
        public string CircuitNumber { get; set; }
        public string DimmingType { get; set; }
        public string FromPanel { get; set; }
        public string ToPanel { get; set; }
    }

    public class RedistributionPlan
    {
        public List<CircuitMove> Moves { get; set; } = new List<CircuitMove>();
        public Dictionary<int, LocationSummaryPair> LocationSummaries { get; set; }
            = new Dictionary<int, LocationSummaryPair>();
        public bool HasChanges => Moves.Count > 0;
    }

    public class LocationSummaryPair
    {
        public int LocationNumber { get; set; }
        public List<PanelSummary> Before { get; set; } = new List<PanelSummary>();
        public List<PanelSummary> After { get; set; } = new List<PanelSummary>();
    }

    public class PanelSummary
    {
        public string PanelName { get; set; }
        public int TotalModules { get; set; }
        public int PanelCapacity { get; set; }
        public bool IsOverCapacity => TotalModules > PanelCapacity;
        public List<TypeSummary> Types { get; set; } = new List<TypeSummary>();
    }

    public class TypeSummary
    {
        public string DimmingType { get; set; }
        public int CircuitCount { get; set; }
        public int ModuleCount { get; set; }
    }
}
