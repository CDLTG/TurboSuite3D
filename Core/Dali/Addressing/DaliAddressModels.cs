#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Addressing
{
    // Pure, Revit-free types for the TurboDALI addressing engine (Phase 2). The engine turns a set of DALI
    // circuits (each one address = one load) plus the designer's loop declarations into concrete
    // "L{loop}-{load##}" labels, lock-aware, all without touching Revit so it is fully unit-testable.
    //
    // Three element sets ride off one model walk (plan H1/H10), and they are DIFFERENT sets on purpose:
    //   • counting   — one load per circuit (DaliLoadCounter, unchanged),
    //   • ordering    — the centroid of a circuit's LIGHTING FIXTURES (the driver device is excluded so its
    //                   arbitrary ceiling spot never drags the spatial walk),
    //   • writing     — EVERY element on the circuit, fixtures AND the driver/decoder device (shim-side).
    // This engine consumes the ordering read (circuit key + zone + fixture centroid) and produces the label;
    // the shim owns the write set (it holds the ElementIds Core can't).

    /// <summary>A Revit-free 2D point (feet, plan X/Y). Circuit centroids are projected to 2D because the
    /// address order must read the way a plan is read; elevation carries no ordering meaning.</summary>
    public readonly struct DaliPoint
    {
        public DaliPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }

        public double DistanceTo(DaliPoint other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>A designer-declared DALI loop, as the addressing engine needs it: a durable
    /// <see cref="LoopId"/> (the L# anchor — a creation-time GUID, never the display name), the display
    /// <see cref="Name"/>, and the member Control Zones in declared order (the outer key of the address
    /// order — see <see cref="DaliAddressReconciler"/>). Built from the reconciled <c>DaliLoopDto</c>.</summary>
    public sealed class DaliLoopInput
    {
        public DaliLoopInput(string loopId, string name, IReadOnlyList<string> zoneNames)
        {
            LoopId = loopId ?? "";
            Name = name ?? "";
            ZoneNames = zoneNames ?? new List<string>();
        }

        public string LoopId { get; }
        public string Name { get; }

        /// <summary>The Control Zones grouped onto this loop's bus, in declared order (the address block order).</summary>
        public IReadOnlyList<string> ZoneNames { get; }
    }

    /// <summary>One DALI circuit as the addressing engine reads it — the identity-preserving sibling read
    /// (plan H1) that <c>DaliLoadCounter</c> throws away. <see cref="CircuitKey"/> is <c>circuit.UniqueId</c>
    /// (the load anchor); <see cref="Zone"/> is the circuit's Control Zone; <see cref="Centroid"/> is the
    /// centroid of its LIGHTING FIXTURES (null ⇒ uncomputable — the walk falls back to a stable-key order).</summary>
    public readonly struct DaliCircuitReading
    {
        public DaliCircuitReading(string circuitKey, string zone, DaliPoint? centroid)
        {
            CircuitKey = (circuitKey ?? "").Trim();
            Zone = (zone ?? "").Trim();
            Centroid = centroid;
        }

        /// <summary>Stable per-circuit handle — <c>circuit.UniqueId</c>. The load-slot anchor.</summary>
        public string CircuitKey { get; }

        /// <summary>The circuit's Control Zone value (which loop it addresses onto). Empty = unaddressable.</summary>
        public string Zone { get; }

        /// <summary>Centroid of the circuit's lighting fixtures. Null ⇒ deterministic key-order fallback.</summary>
        public DaliPoint? Centroid { get; }
    }

    /// <summary>One circuit's resolved address after reconciliation.</summary>
    public sealed class DaliCircuitAddress
    {
        public DaliCircuitAddress(string circuitKey, string zone, string loopId, DaliAddress address)
        {
            CircuitKey = circuitKey;
            Zone = zone;
            LoopId = loopId;
            Address = address;
        }

        public string CircuitKey { get; }
        public string Zone { get; }
        public string LoopId { get; }
        public DaliAddress Address { get; }

        public int LoopNumber => Address.LoopNumber;
        public int LoadNumber => Address.LoadNumber;
        public string Text => Address.Text;
    }

    /// <summary>A lock-aware verdict — a locked change that would mislabel or retire an issued address
    /// (surfaced, never applied silently; the DMX rule).</summary>
    public sealed class DaliReviewItem
    {
        public DaliReviewItem(string circuitKey, string message)
        {
            CircuitKey = circuitKey;
            Message = message;
        }

        public string CircuitKey { get; }
        public string Message { get; }
    }

    /// <summary>The complete addressing for one solve: every addressed circuit's label (in loop→block order),
    /// the loop-number map, and any REVIEWs.</summary>
    public sealed class DaliAddressing
    {
        public DaliAddressing(
            IReadOnlyList<DaliCircuitAddress> addresses,
            IReadOnlyDictionary<string, int> loopNumbers,
            IReadOnlyList<DaliReviewItem> reviews)
        {
            Addresses = addresses;
            LoopNumbers = loopNumbers;
            Reviews = reviews;

            var byCircuit = new Dictionary<string, string>();
            foreach (var a in addresses) byCircuit[a.CircuitKey] = a.Text;
            TextByCircuit = byCircuit;
        }

        /// <summary>Every addressed circuit, in canonical loop→zone-block→spatial order.</summary>
        public IReadOnlyList<DaliCircuitAddress> Addresses { get; }

        /// <summary>LoopId → L# (the level-1 numbering), for the snapshot builder and the loop badges.</summary>
        public IReadOnlyDictionary<string, int> LoopNumbers { get; }

        public IReadOnlyList<DaliReviewItem> Reviews { get; }

        /// <summary><c>circuit.UniqueId → "L2-01"</c> — the write-back lookup the shim keys its element loop on.</summary>
        public IReadOnlyDictionary<string, string> TextByCircuit { get; }

        public bool HasReviews => Reviews.Count > 0;
    }
}
