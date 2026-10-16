#nullable disable
using System.Collections.Generic;

namespace TurboSuite.Driver.Models
{
    /// <summary>A driver type as offered in the match-check dropdown: a display name + its candidate info.</summary>
    public class DriverOption
    {
        public string DisplayName { get; set; }
        public DriverCandidateInfo Driver { get; set; }
    }

    /// <summary>An RPS fixture type as offered in the match-check dropdown: a display name + its spec.</summary>
    public class FixtureOption
    {
        public string DisplayName { get; set; }
        public FixtureData Fixture { get; set; }
    }

    /// <summary>The two type lists the match-check window picks from, collected from the project.</summary>
    public class MatchCatalog
    {
        public List<DriverOption> Drivers { get; set; } = new List<DriverOption>();
        public List<FixtureOption> Fixtures { get; set; } = new List<FixtureOption>();
    }
}
