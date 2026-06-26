#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dmx.Persistence
{
    // Pure, Revit-free DTOs for the TurboDMX document-side ExtensibleStorage payload (the "design-intent
    // overlays" above the read-only model + native Control Zone parameter — see TurboDMX-BuildPlan Phase 0).
    // The whole bundle is serialized to one JSON document parked in the DMX schema's StateJson field
    // (DmxStorageService), so the ES field set stays fixed while these shapes grow across Phases 1–3 —
    // payload migrations ride PayloadVersion instead of forcing a new schema GUID.
    //
    // What lives WHERE (deliberate):
    //   • Control Zone   — a native Revit instance parameter ON the tape, set in Properties; NOT here.
    //                      The doc schema only references zones by their string VALUE.
    //   • These overlays — loops, clusters, control-system tags, and (later) the solve snapshot — have no
    //                      native parameter home, so they live in the doc schema.

    /// <summary>Root of the JSON payload bundle — serialized whole into the DMX schema's StateJson field.</summary>
    public sealed class DmxModuleState
    {
        /// <summary>Payload-shape version for forward migration without a new ES schema GUID. Bump when a
        /// DTO below changes shape; readers upgrade old payloads in code.</summary>
        public int PayloadVersion { get; set; } = 1;

        public DmxSettingsDto Settings { get; set; } = new DmxSettingsDto();

        /// <summary>Designer-declared DMX loops (Zone→Loop). Keyed by Control Zone VALUE, not ElementId.</summary>
        public List<DmxLoopDto> Loops { get; set; } = new List<DmxLoopDto>();

        /// <summary>Physical clusters (decoder-packing grain). Keyed by run ElementId; pruned on solve.</summary>
        public List<DmxClusterDto> Clusters { get; set; } = new List<DmxClusterDto>();

        /// <summary>Control-System (run-unit) tag per DMX fixture. Defaults to one system (empty ⇒ "All").</summary>
        public List<DmxControlSystemTagDto> ControlSystemTags { get; set; } = new List<DmxControlSystemTagDto>();

        /// <summary>Last solve snapshot (Phase 3 — lock-aware re-run safety). Null until first solved.</summary>
        public DmxSnapshotDto? Snapshot { get; set; }
    }

    /// <summary>Module settings — Profile selection + Kind-2 job policy + the curated part pools.
    /// Profile pre-fills these; fields stay overridable (TurboDMX-UI-Structure §1).</summary>
    public sealed class DmxSettingsDto
    {
        public string Profile { get; set; } = "Lutron";

        // Kind-2 job policy (breaker / inrush / segmenting / addressing). Defaults are profile-seeded at
        // author time; 0/empty here means "fall back to the profile default" until Phase 1 wires the panel.
        public double BreakerAmps { get; set; }
        public double BreakerVolts { get; set; }
        public double DeratingFactor { get; set; }
        public int MaxDriversPerBreaker { get; set; }
        public int DevicesPerSegment { get; set; }
        public int BitDepth { get; set; }
        public int ReservedChannels { get; set; }

        /// <summary>Curated decoder family types (Q10) — the job's kit, ticked from discovery. Stored as
        /// stable type identifiers (UniqueId strings) resolved back to symbols at read time.</summary>
        public List<string> DecoderTypeIds { get; set; } = new List<string>();

        /// <summary>Curated driver family types — same gesture/storage as decoders.</summary>
        public List<string> DriverTypeIds { get; set; } = new List<string>();
    }

    /// <summary>One designer-declared DMX loop = one Interface chain = one one-line diagram.</summary>
    public sealed class DmxLoopDto
    {
        public string LoopId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Order { get; set; }

        /// <summary>The Control Zone VALUES grouped into this loop (the native param values).</summary>
        public List<string> ZoneValues { get; set; } = new List<string>();
    }

    /// <summary>One physical cluster within a zone — the runs close enough to share decoders.</summary>
    public sealed class DmxClusterDto
    {
        public string ClusterId { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>The Control Zone VALUE this cluster partitions (clusters live within one zone).</summary>
        public string ZoneValue { get; set; } = "";

        /// <summary>The tape-run element ids bound to this cluster. Pruned on solve; copied runs that fall
        /// out land in the zone's "(unclustered)" residual until re-clustered.</summary>
        public List<long> RunElementIds { get; set; } = new List<long>();
    }

    /// <summary>Per-fixture Control-System (run-unit) tag. Absent ⇒ the single default system.</summary>
    public sealed class DmxControlSystemTagDto
    {
        public long FixtureElementId { get; set; }
        public string ControlSystem { get; set; } = "";
    }

    /// <summary>Solve snapshot for re-run safety + one-line generation (Phase 3/4). Shape intentionally
    /// thin in Phase 0 — Phase 3 fills the placed-decoder/driver identity + issued numbering it needs to
    /// pack only the unbuilt remainder and keep locked numbers fixed.</summary>
    public sealed class DmxSnapshotDto
    {
        /// <summary>Numbering lifecycle: Unlocked → Locked → Re-locked (TurboDMX-Design §8c).</summary>
        public string NumberingState { get; set; } = "Unlocked";

        /// <summary>Reserved for the per-decoder/driver issued-identity records Phase 3 will snapshot.</summary>
        public List<DmxSnapshotItemDto> Items { get; set; } = new List<DmxSnapshotItemDto>();
    }

    /// <summary>Placeholder for one snapshotted, placed device's stable identity + issued number (Phase 3).</summary>
    public sealed class DmxSnapshotItemDto
    {
        public long ElementId { get; set; }
        public string IssuedNumber { get; set; } = "";
    }
}
