#nullable enable
using TurboSuite.Dali.Addressing;

namespace TurboSuite.Dali.Input
{
    /// <summary>The kind of addressable DALI unit. Drives both the durable-key shape and the ordering: a
    /// downlight is a single self-driven fixture at a real location; a driver is a remote power supply whose
    /// own model XYZ is a Field-Locate-Me artifact (never walked), ordered instead by its within-circuit
    /// ordinal at its circuit's fixture centroid.</summary>
    public enum DaliUnitKind
    {
        Downlight,
        Driver,
    }

    /// <summary>
    /// One <b>addressable DALI unit</b> as read from the model — the grain that gets exactly one
    /// <c>L{loop}-{##}</c> address and counts as exactly one bus load. Replaces the old per-circuit
    /// <c>DaliFixtureReading</c> (which collapsed N drivers on a circuit to one address). Two kinds:
    ///
    /// <list type="bullet">
    ///   <item><b>Driver</b> — a valid remote power supply device on a driver-bearing circuit (one per
    ///   driver device); its tape fixtures carry no address.</item>
    ///   <item><b>Downlight</b> — a self-driven DALI fixture on a circuit with no driver device.</item>
    /// </list>
    ///
    /// Revit-free: the shim owns the ElementIds and re-resolves the live write target from
    /// <see cref="UnitKey"/> (a driver redeploy makes fresh instances, so the key is NEVER the driver
    /// element's UniqueId — see the plan's durable-key amendment).
    /// </summary>
    public readonly struct DaliUnitReading
    {
        public DaliUnitReading(string? unitKey, string? circuitKey, DaliUnitKind kind,
                               int ordinal, string? zone, DaliPoint? centroid)
        {
            UnitKey = (unitKey ?? "").Trim();
            CircuitKey = (circuitKey ?? "").Trim();
            Kind = kind;
            Ordinal = ordinal;
            Zone = (zone ?? "").Trim();
            Centroid = centroid;
        }

        /// <summary>Durable reconcile/lock anchor — survives a driver redeploy. Drivers:
        /// <c>circuit.UniqueId + "#" + ordinal</c> (the circuit is not recreated on a redeploy and the ordinal
        /// is the deterministic down-column index); downlights: the fixture UniqueId. "" = unaddressable.</summary>
        public string UnitKey { get; }

        /// <summary>The unit's circuit (<c>circuit.UniqueId</c>); "" when uncircuited. Groups a circuit's
        /// several driver units and carries the shared circuit-level walk <see cref="Centroid"/>.</summary>
        public string CircuitKey { get; }

        public DaliUnitKind Kind { get; }

        /// <summary>Within-circuit down-column index (driver suffix a→0, b→1…; 0 for a downlight or a single
        /// unsuffixed driver). Orders a circuit's driver units top-to-bottom.</summary>
        public int Ordinal { get; }

        /// <summary>The unit's Control Zone (a driver inherits it from its circuit's fixtures). "" = unzoned
        /// (present hardware, but joins no loop).</summary>
        public string Zone { get; }

        /// <summary>Circuit-level walk anchor — the centroid of the circuit's tape/downlight fixtures (a
        /// downlight's is its own location). Null ⇒ deterministic key-order fallback. Shared by every driver
        /// unit on the same circuit so they stay contiguous at that circuit's walked spot.</summary>
        public DaliPoint? Centroid { get; }
    }
}
