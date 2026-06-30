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

        /// <summary>Placement registry (Phase 3 Option-A cleanup): every placed decoder's DEC # + its decoder
        /// and paired-driver element ids, so a re-Place can delete an orphaned pair exactly. Grows on Place,
        /// pruned when an orphan is removed.</summary>
        public List<DmxPlacedPairDto> Placed { get; set; } = new List<DmxPlacedPairDto>();

        /// <summary>One-line view registry (Phase 4): interface # → the owned Drafting View's element id, so a
        /// re-draw finds + wipes the same view (the program owns it). Written on Draw.</summary>
        public List<DmxOneLineViewDto> OneLineViews { get; set; } = new List<DmxOneLineViewDto>();

        /// <summary>The single per-job wire-legend Drafting View's element id (Phase 6), so a re-draw finds +
        /// wipes the same view. 0 ⇒ never drawn. One per job (not per loop).</summary>
        public long WireLegendViewId { get; set; }
    }

    /// <summary>One loop's owned one-line Drafting View, keyed by its interface #.</summary>
    public sealed class DmxOneLineViewDto
    {
        public int InterfaceNumber { get; set; }
        public long ViewId { get; set; }
    }

    /// <summary>One placed decoder+driver pair in the registry, keyed by DEC #.</summary>
    public sealed class DmxPlacedPairDto
    {
        public int Dec { get; set; }
        public long DecoderId { get; set; }
        public long DriverId { get; set; }   // 0 ⇒ no paired driver
    }

    /// <summary>Module settings — Profile selection + Kind-2 job policy + the curated part pools. Mirrors
    /// the live <c>DmxJobSettings</c> (the editable panel knobs) one-to-one, plus the selected profile and
    /// the curated part-pool ticks. Defaults match <c>DmxJobSettings</c>'s so a never-saved state round-trips
    /// to the same sensible values the window opens with (BuildPlan Phase 2).</summary>
    public sealed class DmxSettingsDto
    {
        public string Profile { get; set; } = "Lutron";

        // Kind-2 job policy (breaker / inrush / segmenting). Defaults mirror DmxJobSettings so applying a
        // fresh (never-saved) DTO is a no-op rather than zeroing the window's seeded values.
        public double SystemVolts { get; set; } = 24.0;
        public double BreakerAmps { get; set; } = 20.0;
        public double FeedVolts { get; set; } = 120.0;
        public double BreakerContinuousDerate { get; set; } = 0.8;
        public int MaxDriversPerBreaker { get; set; }          // 0 = no inrush count cap
        public int MaxDevicesPerSegment { get; set; } = 32;    // D4
        public int ReservedChannels { get; set; }

        /// <summary>Job-wide homerun pull-up (Phase 6): stock sizes to bump every LV homerun past exact. 0 = exact.</summary>
        public int PullUpSizes { get; set; }

        /// <summary>Breaker pack basis (<c>ConnectedLoad</c> / <c>DriverRating</c>) — stored as the enum name.</summary>
        public string BreakerBasis { get; set; } = "ConnectedLoad";

        /// <summary>Curated decoder family types (Q10) — the job's kit, ticked from discovery. Stored as
        /// stable type identifiers (UniqueId strings) resolved back to symbols at read time. EMPTY ⇒ never
        /// curated ⇒ the window's default "all discovered selected" (a valid solve always has ≥1 ticked).</summary>
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

    /// <summary>Solve snapshot for re-run safety (Phase 3 numbering lock, §8c). The lock baseline is
    /// **Control-Zone-anchored** (decision 2026-06-26): per zone, the issued Interface # + ordered DEC #s +
    /// decoder type at the moment of Lock. A locked re-run pins each zone to its baseline numbers; additive
    /// decoders append past the high-water mark; a zone whose decoder TYPE or Interface # changed surfaces as
    /// REVIEW (its issued DEC #s would mislabel installed hardware) — never a silent renumber.</summary>
    public sealed class DmxSnapshotDto
    {
        /// <summary>Numbering lifecycle (TurboDMX-Design §8c). Persisted values: "Unlocked" / "Locked"
        /// (a Re-lock just re-captures <see cref="Zones"/> while staying Locked).</summary>
        public string NumberingState { get; set; } = "Unlocked";

        /// <summary>The frozen per-zone numbering baseline captured at Lock — empty while Unlocked.</summary>
        public List<DmxSnapshotZoneDto> Zones { get; set; } = new List<DmxSnapshotZoneDto>();
    }

    /// <summary>One zone's issued numbering in the lock baseline, keyed by Control Zone value.</summary>
    public sealed class DmxSnapshotZoneDto
    {
        /// <summary>The Control Zone VALUE — the stable, designer-assigned anchor (not an ElementId).</summary>
        public string ZoneValue { get; set; } = "";

        /// <summary>The interface (loop) this zone was addressed onto when locked.</summary>
        public int InterfaceNumber { get; set; }

        /// <summary>The decoder type name driving this zone when locked (a change ⇒ REVIEW).</summary>
        public string DecoderType { get; set; } = "";

        /// <summary>The issued DEC numbers for this zone's decoders, in pack order.</summary>
        public List<int> DecIds { get; set; } = new List<int>();
    }
}
