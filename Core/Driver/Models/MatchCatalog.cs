#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;

namespace TurboSuite.Driver.Models
{
    /// <summary>A driver family as offered in the Driver Match dropdown, with all its driver types.</summary>
    public class DriverFamilyOption
    {
        public ElementRef FamilyRef { get; set; }
        public string FamilyName { get; set; }

        /// <summary>True for the TBD placeholder family — the wildcard, never a link target.</summary>
        public bool IsTbd { get; set; }

        /// <summary>The family's driver types, sorted by Power ascending (the order TurboDriver sizes in).</summary>
        public List<DriverCandidateInfo> Types { get; set; } = new List<DriverCandidateInfo>();

        public string DisplayName => FamilyName;
    }

    /// <summary>An RPS fixture type as offered in the match-check dropdown: a display name + its spec.</summary>
    public class FixtureOption
    {
        public string DisplayName { get; set; }

        /// <summary>The fixture type (<c>FamilySymbol</c>) — what a driver-family link is keyed on.</summary>
        public ElementRef TypeRef { get; set; }

        public FixtureData Fixture { get; set; }
    }

    /// <summary>What the Driver Match tab picks from, collected from the project: driver families, RPS
    /// fixture types, and the project's fixture-type → driver-family links.</summary>
    public class MatchCatalog
    {
        public List<DriverFamilyOption> Families { get; set; } = new List<DriverFamilyOption>();
        public List<FixtureOption> Fixtures { get; set; } = new List<FixtureOption>();
        public List<DriverLink> Links { get; set; } = new List<DriverLink>();
    }
}
