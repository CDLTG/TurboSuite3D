#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Persistence
{
    // Pure, Revit-free DTOs for the TurboDALI document-side ExtensibleStorage payload — the designer's
    // declared DALI loops, layered above the read-only model + the native Control Zone parameter (the same
    // division TurboDMX uses; see Core/Dmx/Persistence/DmxPersistenceModels.cs).
    //
    // Deliberately far smaller than DmxModuleState. A DALI loop is only a named, ordered grouping of Control
    // Zone VALUES; there is no cluster/decoder-packing grain, no placement or one-line registry, and no
    // numbering-lock snapshot (those DMX features are reserved for a future TurboDALI — see the plan's §3e).
    // Keep this lean: do NOT port DMX fields that carry no DALI meaning. When DALI later grows a field, ride
    // PayloadVersion for the migration rather than minting a new schema GUID (DaliStorageService).
    //
    // What lives WHERE (deliberate, same as DMX):
    //   • Control Zone — a native Revit instance parameter ON the fixture, set in Properties; NOT here.
    //                    This schema only references zones by their string VALUE.
    //   • Loops        — the designer's zone→loop grouping has no native parameter home, so it lives here.

    /// <summary>Root of the JSON payload bundle — serialized whole into the DALI schema's StateJson field.</summary>
    public sealed class DaliModuleState
    {
        /// <summary>Payload-shape version for forward migration without a new ES schema GUID. Bump when a
        /// DTO below changes shape; readers upgrade old payloads in code.
        /// <para>v2 (Phase 3e): <see cref="DaliLoopDto.AssignedZone"/> added. A v1 payload deserializes it
        /// to 0 = unassigned, which is the safe default — the loop is still ordered job-wide, just not
        /// placed in a panel, and it surfaces as an "unassigned loop" warning rather than silently vanishing.</para></summary>
        public int PayloadVersion { get; set; } = 2;

        /// <summary>Designer-declared DALI loops (Zone→Loop). Keyed by Control Zone VALUE, not ElementId.</summary>
        public List<DaliLoopDto> Loops { get; set; } = new List<DaliLoopDto>();
    }

    /// <summary>One designer-declared DALI loop = one DALI bus = one <c>LQSE2-1DALUNV-D</c> module.</summary>
    public sealed class DaliLoopDto
    {
        public string LoopId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Order { get; set; }

        /// <summary>The Control Zone VALUES grouped into this loop (the native param values).</summary>
        public List<string> ZoneValues { get; set; } = new List<string>();

        /// <summary>The ZONE N (panel LocationNumber) the designer assigned this loop's module to
        /// (Phase 3e). <b>0 = unassigned</b>: the loop is still ordered by the job-wide DALI demand, but has
        /// no panel to sit in, so the TurboZones DALI tab warns and the allocator places no slot for it.
        /// Placement is display-only — a loop's fixtures are all within one zone, but DALI circuits are
        /// created unassigned (like DMX), so the zone can't be derived and the designer must pick it.</summary>
        public int AssignedZone { get; set; }
    }
}
