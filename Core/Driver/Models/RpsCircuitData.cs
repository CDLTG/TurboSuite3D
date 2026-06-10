#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Services;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Revit-free per-circuit DTO for the TurboRPS staleness dashboard. Built shim-side
    /// (collect → recommend → classify) and consumed by the Core ViewModels. Mirrors the
    /// TurboZones <c>ZonesCircuitData</c> pattern.
    /// </summary>
    public class RpsCircuitData
    {
        public ElementRef CircuitRef { get; set; }
        public string CircuitNumber { get; set; }
        public string LoadName { get; set; }
        public string DimmingProtocol { get; set; }

        /// <summary>Full circuit apparent load (all fixtures, incl. line-voltage downlights on a
        /// relay circuit). Retained for reference; the dashboard surfaces <see cref="RpsLoadWatts"/>.</summary>
        public double ApparentPower { get; set; }

        /// <summary>Total wattage of the RPS fixtures only — the load the driver actually serves,
        /// matching the detail pane's sub-driver packing. This is what the "Power Supply Review"
        /// grid shows.</summary>
        public double RpsLoadWatts { get; set; }

        public string Panel { get; set; }

        /// <summary>Placed driver instances on this circuit (drivers only). These are the
        /// elements an in-place swap retypes.</summary>
        public List<ElementRef> DeviceRefs { get; set; } = new List<ElementRef>();

        /// <summary>Display name of the single placed driver type; empty when none/mixed.</summary>
        public string PlacedTypeName { get; set; }

        /// <summary>Switch IDs (driver numbers) of the placed drivers on this circuit, in placement
        /// order — e.g. <c>X07a</c>, <c>X07b</c>. TurboDriver assigns a per-circuit base and suffixes
        /// each physical driver. Surfaced in the grid's "Switch IDs" column and matched by the search box.</summary>
        public List<string> SwitchIds { get; set; } = new List<string>();

        public int PlacedCount { get; set; }
        public int DistinctPlacedTypeCount { get; set; }
        public int PlacedChannels { get; set; }

        public DriverRecommendation Recommendation { get; set; }
        public RpsStatus Status { get; set; }

        /// <summary>Non-null only when <see cref="Status"/> is <see cref="RpsStatus.Rebuild"/>.</summary>
        public string RebuildReason { get; set; }

        /// <summary>True when the fresh recommendation splits a line-based fixture across taps —
        /// an info-only signal that the physical tape cut-list is also stale (TurboDriver job).
        /// Does not affect swappability.</summary>
        public bool HasSplitSegments { get; set; }

        /// <summary>Recommended type to swap every placed driver to (Case-A). <see cref="ElementRef.None"/>
        /// when there is no auto-applicable recommendation.</summary>
        public ElementRef RecommendedTypeRef { get; set; }

        /// <summary>Recommended type display name, for the grid's "Recommended" column.</summary>
        public string RecommendedTypeName { get; set; }

        /// <summary>Recommended physical driver count.</summary>
        public int RecommendedCount { get; set; }

        public List<FixtureData> Fixtures { get; set; } = new List<FixtureData>();

        /// <summary>User has intentionally deferred this circuit — a knowingly "incorrect" driver
        /// config kept for external reasons. Deferred circuits render neutral (not amber/red), are
        /// excluded from the issue counts and batch-correct, and hidden by "Show only issues".
        /// Persisted in ExtensibleStorage on the circuit element (see RpsDeferralStorageService).</summary>
        public bool IsDeferred { get; set; }

        /// <summary>The config signature (<see cref="RpsDeferral.Signature"/>) captured when the
        /// circuit was deferred. Compared against the live signature on each scan: a mismatch means
        /// the circuit changed since deferral, so the row is surfaced for review instead of staying
        /// silently neutral. Null when not deferred.</summary>
        public string DeferredSignature { get; set; }
    }

    /// <summary>One in-place driver retype: set <see cref="DeviceRef"/>'s symbol to
    /// <see cref="NewTypeRef"/>. Batched into a single transaction by
    /// <c>IRpsRevitOperations.SwapDriverTypes</c>.</summary>
    public class DriverSwap
    {
        public ElementRef DeviceRef { get; set; }
        public ElementRef NewTypeRef { get; set; }
    }
}
