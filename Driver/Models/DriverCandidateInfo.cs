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

        /// <summary>
        /// Derating factor (0,1] applied to the per-sub-driver packing capacity.
        /// Defaults to 1.0 (no derate). Never feeds the validity / sub-driver-count
        /// math, which must use the rated SubDriverPower.
        /// </summary>
        public double DerateFactor { get; set; } = 1.0;
    }
}
