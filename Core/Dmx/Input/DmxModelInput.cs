#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.Input
{
    // The boundary types between the Revit-coupled reader (Shim/Dmx/Services/DmxModelReader) and the pure
    // Core layer (zone builder, contract builder, the main ViewModel). The reader populates these from the
    // active document with ZERO model writes (TurboDMX-BuildPlan Phase 1); Core consumes them blind to Revit.

    /// <summary>One DMX lighting-fixture instance as read from the model — the engine's <c>TapeRun</c> raw
    /// material plus the native <c>Control Zone</c> value that groups it (TurboDMX-BuildPlan Phase 1).</summary>
    public sealed class DmxFixtureReading
    {
        /// <summary>Host-document ElementId of the fixture instance (for selection / cluster binding later).</summary>
        public long ElementId { get; set; }

        /// <summary>The native <c>Control Zone</c> instance-parameter value. Empty ⇒ unassigned (excluded
        /// from the solve, surfaced as a count).</summary>
        public string ControlZone { get; set; } = "";

        /// <summary>DMX channel count read from the family (1 single … 6 RGBATW). No color model in code.</summary>
        public int Channels { get; set; }

        /// <summary>Tape run length in feet (the engine works in feet).</summary>
        public double LengthFt { get; set; }

        /// <summary>Watts per foot for this run (from the family/type).</summary>
        public double WattsPerFt { get; set; }
    }

    /// <summary>A discovered decoder family type and its caps (Kind-1 part properties, read off the family).
    /// The designer ticks the job's kit from these (Q10 curated-from-discovery).</summary>
    public sealed class DmxDecoderCandidate
    {
        /// <summary>Stable type identity (UniqueId) for persistence + re-resolution.</summary>
        public string TypeId { get; set; } = "";
        public string Name { get; set; } = "";
        public int MaxOutputs { get; set; }
        public double MaxAmpsPerOutput { get; set; }
        public double MaxWatts { get; set; }
    }

    /// <summary>A discovered driver family type and its caps (Kind-1 part properties, read off the family).</summary>
    public sealed class DmxDriverCandidate
    {
        public string TypeId { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>The family's Type Mark (e.g. "CV"/"MD"/"ME") — the label the one-line driver box shows.
        /// Distinct from <see cref="Name"/> ("Family : Type"); empty if the family carries no Type Mark.</summary>
        public string TypeMark { get; set; } = "";

        public double RatedWatts { get; set; }
        public double OperatingVolts { get; set; }
        public double DeratingFactorRaw { get; set; }
    }

    /// <summary>The full read-only snapshot the reader hands the window: every DMX fixture, plus the
    /// discovered decoder/driver candidate pools. The window groups, declares loops, and solves off this.</summary>
    public sealed class DmxModelSnapshot
    {
        public IReadOnlyList<DmxFixtureReading> Fixtures { get; set; } = new List<DmxFixtureReading>();
        public IReadOnlyList<DmxDecoderCandidate> DecoderCandidates { get; set; } = new List<DmxDecoderCandidate>();
        public IReadOnlyList<DmxDriverCandidate> DriverCandidates { get; set; } = new List<DmxDriverCandidate>();
    }
}
