#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali
{
    /// <summary>
    /// A designer-declared DALI loop: a named, ordered grouping of Control Zones that share ONE DALI bus
    /// (one <c>LQSE2-1DALUNV-D</c> module). This is the engine-input analog of the DMX
    /// <see cref="TurboSuite.Dmx.LoopDeclaration"/>, deliberately narrower — a DALI loop has no interface
    /// channel budget, so there is no <c>ReservedChannels</c> knob; it is only a name plus its zones.
    ///
    /// The grain (module <c>LQSE2-1DALUNV-D</c> NA, 1 bus/module): <b>module count = loop count</b>, and each
    /// DALI-addressable load in a loop is one switch leg. <c>DaliSolver</c> consumes these declarations to emit
    /// the subsystem's demand (modules → panel slots, loads → link legs).
    /// </summary>
    public sealed class DaliLoopDeclaration
    {
        public DaliLoopDeclaration(string name, IReadOnlyList<string> zoneNames)
        {
            Name = name;
            ZoneNames = zoneNames;
        }

        public string Name { get; }

        /// <summary>The Control Zones (by name) this loop groups onto one DALI bus, in declared order.</summary>
        public IReadOnlyList<string> ZoneNames { get; }
    }
}
