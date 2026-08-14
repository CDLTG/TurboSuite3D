#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Persistence
{
    // Pure, Revit-free DTOs for the TurboDALI document-side ExtensibleStorage payload — the designer's
    // declared DALI loops, layered above the read-only model + the native Control Zone parameter (the same
    // division TurboDMX uses; see Core/Dmx/Persistence/DmxPersistenceModels.cs).
    //
    // Deliberately far smaller than DmxModuleState. A DALI loop is only a named, ordered grouping of Control
    // Zone VALUES; there is no cluster/decoder-packing grain and no one-line registry. Keep this lean: do NOT
    // port DMX fields that carry no DALI meaning. When DALI grows a field, ride PayloadVersion for the
    // migration rather than minting a new schema GUID (DaliStorageService).
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
        /// <para>v2: <see cref="DaliLoopDto.AssignedZone"/> added. A v1 payload deserializes it
        /// to 0 = unassigned, which is the safe default — the loop is still ordered job-wide, just not
        /// placed in a panel, and it surfaces as an "unassigned loop" warning rather than silently vanishing.</para>
        /// <para>v3: <see cref="Snapshot"/> added — the addressing numbering-lock flag + frozen
        /// baseline. A v2 payload deserializes it to null = Unlocked/unaddressed, the safe default (the job
        /// simply has no issued addresses yet). Tolerant read means an OLD v2 reader seeing a v3 payload just
        /// ignores the field it doesn't know — the loops it does need are untouched (guarded by a
        /// v3→v2 characterization test).</para></summary>
        public int PayloadVersion { get; set; } = 2;

        /// <summary>Designer-declared DALI loops (Zone→Loop). Keyed by Control Zone VALUE, not ElementId.</summary>
        public List<DaliLoopDto> Loops { get; set; } = new List<DaliLoopDto>();

        /// <summary>The addressing numbering-lock state + frozen baseline (v3, TurboDALI). Null while the job
        /// is unaddressed / unlocked — addresses churn freely from the live spatial walk until Lock captures
        /// this baseline. Mirrors <c>DmxModuleState.Snapshot</c>.</summary>
        public DaliSnapshotDto? Snapshot { get; set; }
    }

    /// <summary>The frozen addressing baseline captured at Lock (v3). Empty <see cref="Circuits"/> +
    /// <c>NumberingState="Unlocked"</c> while the job churns; a Lock freezes every issued
    /// <c>L{loop}-{load##}</c> so a later re-walk never moves an already-issued number. Mirrors
    /// <c>DmxSnapshotDto</c>, two-level (loop + load) because a DALI address is two numbers.</summary>
    public sealed class DaliSnapshotDto
    {
        /// <summary>Numbering lifecycle. Persisted values: "Unlocked" / "Locked" (a Re-lock re-captures the
        /// baseline while staying Locked).</summary>
        public string NumberingState { get; set; } = "Unlocked";

        /// <summary>Per-loop issued L# at lock (the level-1 anchor: <c>LoopId → L#</c>).</summary>
        public List<DaliSnapshotLoopDto> Loops { get; set; } = new List<DaliSnapshotLoopDto>();

        /// <summary>Per-circuit issued slot at lock (the level-2 anchor: <c>circuit.UniqueId → (loop, L#,
        /// load##)</c>), denormalized with the L# + zone so a retired-circuit REVIEW can name the exact
        /// address that was issued without re-deriving it.</summary>
        public List<DaliSnapshotCircuitDto> Circuits { get; set; } = new List<DaliSnapshotCircuitDto>();
    }

    /// <summary>One loop's issued L# in the lock baseline, keyed by the durable <see cref="LoopId"/>.</summary>
    public sealed class DaliSnapshotLoopDto
    {
        public string LoopId { get; set; } = "";
        public int LoopNumber { get; set; }
    }

    /// <summary>One circuit's issued address in the lock baseline, keyed by <see cref="CircuitKey"/>
    /// (<c>circuit.UniqueId</c>). Carries its lock-time loop + L# + zone so a moved/retired circuit can be
    /// flagged and named precisely.</summary>
    public sealed class DaliSnapshotCircuitDto
    {
        public string CircuitKey { get; set; } = "";
        public string LoopId { get; set; } = "";
        public int LoopNumber { get; set; }
        public int LoadNumber { get; set; }
        public string Zone { get; set; } = "";
    }

    /// <summary>One designer-declared DALI loop = one DALI bus = one <c>LQSE2-1DALUNV-D</c> module.</summary>
    public sealed class DaliLoopDto
    {
        public string LoopId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Order { get; set; }

        /// <summary>The Control Zone VALUES grouped into this loop (the native param values).</summary>
        public List<string> ZoneValues { get; set; } = new List<string>();

        /// <summary>The ZONE N (panel LocationNumber) the designer assigned this loop's module to.
        /// <b>0 = unassigned</b>: the loop is still ordered by the job-wide DALI demand, but has
        /// no panel to sit in, so TurboDALI warns and the allocator places no slot for it.
        /// Placement is display-only — a loop's fixtures are all within one zone, but DALI circuits are
        /// created unassigned (like DMX), so the zone can't be derived and the designer must pick it.</summary>
        public int AssignedZone { get; set; }
    }
}
