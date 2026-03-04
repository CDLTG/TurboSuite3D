#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Represents a Lighting Device type's driver capabilities extracted from its parameters
    /// </summary>
    public class DriverCandidateInfo
    {
        public FamilySymbol FamilySymbol { get; set; }
        public string FamilyTypeName { get; set; }
        public string Manufacturer { get; set; }
        public double TotalPower { get; set; }
        public double SubDriverPower { get; set; }
        public int SubDriverCount { get; set; }
        public bool IsValidDriver { get; set; }
        public string DimmingProtocol { get; set; }
        public int MaximumFixtures { get; set; }
        public string Voltage { get; set; }
    }
}
