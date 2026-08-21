#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Addressing
{
    // Pure, Revit-free types for the TurboDALI addressing engine. The engine turns a set of addressable DALI
    // UNITS (each one = one DALI address = one bus load — a driver device or a self-driven downlight; see
    // DaliUnitReading) plus the designer's loop declarations into concrete "L{loop}-{##}" labels, lock-aware,
    // all without touching Revit so it is fully unit-testable. The ## is the DALI short address, zero-based.
    //
    // The unit enumeration is the shim's DaliUnitEnumerator (the same walk the load counter consumes). This
    // engine consumes those unit readings (durable unit key + zone + ordinal + circuit centroid) and produces
    // the label; the shim owns the write set (it re-resolves each unit's live element — the ElementIds Core
    // can't hold).

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

    // NOTE: the per-unit model read (durable unit key + zone + kind + ordinal + circuit centroid) is
    // TurboSuite.Dali.Input.DaliUnitReading — the shared type the load counter and the addressing reader both
    // consume, so demand and addressing can't diverge. The reconciler takes those readings directly.

    /// <summary>One addressable unit's resolved address after reconciliation.</summary>
    public sealed class DaliUnitAddress
    {
        public DaliUnitAddress(string unitKey, string zone, string loopId, DaliAddress address)
        {
            UnitKey = unitKey;
            Zone = zone;
            LoopId = loopId;
            Address = address;
        }

        /// <summary>The durable unit key (driver: <c>circuit.UniqueId#ordinal</c>; downlight: fixture
        /// UniqueId) — the reconcile/lock anchor and the shim's write-target handle.</summary>
        public string UnitKey { get; }
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
        public DaliReviewItem(string unitKey, string message)
        {
            UnitKey = unitKey;
            Message = message;
        }

        public string UnitKey { get; }
        public string Message { get; }
    }

    /// <summary>The complete addressing for one solve: every addressed unit's label (in loop→zone-block→
    /// spatial order), the loop-number map, and any REVIEWs.</summary>
    public sealed class DaliAddressing
    {
        public DaliAddressing(
            IReadOnlyList<DaliUnitAddress> addresses,
            IReadOnlyDictionary<string, int> loopNumbers,
            IReadOnlyList<DaliReviewItem> reviews)
        {
            Addresses = addresses;
            LoopNumbers = loopNumbers;
            Reviews = reviews;

            var byUnit = new Dictionary<string, string>();
            foreach (var a in addresses) byUnit[a.UnitKey] = a.Text;
            TextByUnit = byUnit;
        }

        /// <summary>Every addressed unit, in canonical loop→zone-block→spatial order.</summary>
        public IReadOnlyList<DaliUnitAddress> Addresses { get; }

        /// <summary>LoopId → L# (the level-1 numbering), for the snapshot builder and the loop badges.</summary>
        public IReadOnlyDictionary<string, int> LoopNumbers { get; }

        public IReadOnlyList<DaliReviewItem> Reviews { get; }

        /// <summary><c>unitKey → "L2-00"</c> — the write-back lookup the shim resolves to a live element.</summary>
        public IReadOnlyDictionary<string, string> TextByUnit { get; }

        public bool HasReviews => Reviews.Count > 0;
    }
}
