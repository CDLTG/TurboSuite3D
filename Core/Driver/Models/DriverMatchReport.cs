#nullable disable
using System.Collections.Generic;

namespace TurboSuite.Driver.Models
{
    /// <summary>How a single field compared. Drives the glyph/colour in the match-check grid.</summary>
    public enum MatchVerdict
    {
        /// <summary>Field matches / is satisfied.</summary>
        Pass,
        /// <summary>Field fails. On a hard-gate check, this blocks the match.</summary>
        Fail,
        /// <summary>Field differs but does not block (soft preference, e.g. Manufacturer).</summary>
        Warn,
        /// <summary>Informational only — never affects the verdict (e.g. wattage, TBD wildcard).</summary>
        Info
    }

    /// <summary>One row of the comparison: a field, the two sides' values, the verdict, and why.</summary>
    public class MatchCheck
    {
        public string Field { get; set; }
        public string FixtureValue { get; set; }
        public string DriverValue { get; set; }
        public MatchVerdict Verdict { get; set; }
        public string Reason { get; set; }

        /// <summary>
        /// True when a <see cref="MatchVerdict.Fail"/> on this check blocks the match (the hard gates
        /// TurboDriver applies: valid driver, Voltage, Dimming Protocol). Soft/informational checks
        /// (Manufacturer, wattage) are false — they can never flip the verdict.
        /// </summary>
        public bool IsHardGate { get; set; }
    }

    /// <summary>The full field-by-field comparison of one driver type against one RPS fixture type.</summary>
    public class DriverMatchReport
    {
        public List<MatchCheck> Checks { get; set; } = new List<MatchCheck>();

        /// <summary>True when no hard gate failed — i.e. this driver would serve this fixture.</summary>
        public bool Matches { get; set; }

        /// <summary>One-line plain-language verdict for the summary bar.</summary>
        public string Summary { get; set; }
    }
}
