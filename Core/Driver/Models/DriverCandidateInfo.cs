#nullable disable
using TurboSuite.Abstractions;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Represents a Lighting Device type's driver capabilities extracted from its parameters
    /// </summary>
    public class DriverCandidateInfo
    {
        /// <summary>
        /// Revit-free handle to the driver's <c>FamilySymbol</c>. Shim code resolves the
        /// concrete symbol via <c>doc.GetElement(SymbolRef.ToElementId())</c> for placement
        /// and the family-type dropdown.
        /// </summary>
        public ElementRef SymbolRef { get; set; }

        /// <summary>
        /// The driver's Catalog Number1, pre-extracted at collection time so the selection
        /// algorithm stays Revit-free (it once read this off the live <c>FamilySymbol</c>).
        /// </summary>
        public string CatalogNumber { get; set; }

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

        /// <summary>
        /// True when this type belongs to the TBD placeholder family (family name contains
        /// "TBD" — e.g. AL_RPS_TBD). TBD is the only wildcard: it bypasses the Voltage /
        /// Dimming Protocol hard filters and is always ranked strictly last, so it surfaces
        /// only when no real driver matches.
        /// </summary>
        public bool IsTbd { get; set; }
    }
}
