#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Result of the driver selection algorithm
    /// </summary>
    public class DriverRecommendation
    {
        public int DriverCount { get; set; }
        public string DriverType { get; set; }
        public int SubDriversPerDriver { get; set; }
        public int TotalSubDrivers { get; set; }
        public string DisplayText { get; set; }
        public List<SubDriverAssignment> SubDriverAssignments { get; set; } = new List<SubDriverAssignment>();
        public DriverCandidateInfo RecommendedCandidate { get; set; }
        public bool HasMatch { get; set; }
        public string WarningMessage { get; set; }
    }

    /// <summary>
    /// A single sub-driver and the fixture segments assigned to it
    /// </summary>
    public class SubDriverAssignment
    {
        public int SubDriverIndex { get; set; }
        public int DriverIndex { get; set; }
        public double TotalLoad { get; set; }
        public double Capacity { get; set; }
        public List<FixtureSegment> Segments { get; set; } = new List<FixtureSegment>();
    }

    /// <summary>
    /// A fixture segment (may be a split portion of a fixture)
    /// </summary>
    public class FixtureSegment
    {
        public ElementId FixtureId { get; set; }
        public string TypeMark { get; set; }
        public double Wattage { get; set; }
        public bool IsSplit { get; set; }
        public double OriginalWattage { get; set; }
        public string SplitLabel { get; set; }
        public double LinearLength { get; set; }
    }
}
