using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliAddressReconciler"/> — the two-level (loop L# + short address ##) lock-aware
    /// addressing reconcile over addressable UNITS, the DALI analog of
    /// <see cref="TurboSuite.Tests.Dmx.DmxLockReconcilerTests"/>. Covers Fresh (zone-block → NW-seeded circuit
    /// walk, driver circuits expanded by ordinal, contiguous zero-based ##), Pinned (kept slots, gap-safe
    /// append, deleted/moved REVIEWs, silent within-loop zone move), and driver-key redeploy stability. Pure —
    /// synthetic loops + unit readings, no Revit.
    /// </summary>
    public class DaliAddressReconcilerTests
    {
        private static DaliLoopInput Loop(string id, params string[] zones) =>
            new DaliLoopInput(id, id, zones);

        // A single-unit (downlight) circuit: unit key == circuit key, at (x,y).
        private static DaliUnitReading U(string key, string zone, double x, double y) =>
            new DaliUnitReading(key, key, DaliUnitKind.Downlight, 0, zone, new DaliPoint(x, y));

        // A point-less downlight unit.
        private static DaliUnitReading Unp(string key, string zone) =>
            new DaliUnitReading(key, key, DaliUnitKind.Downlight, 0, zone, null);

        // A driver unit at `ord` on circuit `circuit`, sharing that circuit's fixture centroid (x,y).
        private static DaliUnitReading Drv(string circuit, int ord, string zone, double x, double y) =>
            new DaliUnitReading(circuit + "#" + ord, circuit, DaliUnitKind.Driver, ord, zone, new DaliPoint(x, y));

        private static DaliAddressing Fresh(IEnumerable<DaliLoopInput> loops, IEnumerable<DaliUnitReading> units) =>
            DaliAddressReconciler.Reconcile(loops.ToList(), units.ToList(), null, false);

        private static DaliAddressing Locked(
            IEnumerable<DaliLoopInput> loops, IEnumerable<DaliUnitReading> units, DaliSnapshotDto baseline) =>
            DaliAddressReconciler.Reconcile(loops.ToList(), units.ToList(), baseline, true);

        private static string Addr(DaliAddressing a, string key) => a.TextByUnit[key];

        // ── Fresh (unlocked) ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Fresh_SingleZone_NumbersInNwWalkOrder()
        {
            // c2 sits north of c1 ⇒ NW seed gives c2 = L1-00, c1 = L1-01 (zero-based).
            var loops = new[] { Loop("L1", "Kitchen") };
            var units = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10) };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "c2"));
            Assert.Equal("L1-01", Addr(a, "c1"));
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Fresh_ZonesAreContiguousBlocks_InDeclaredOrder()
        {
            // Loop groups [Kitchen, Bar] in that declared order. Even though the Bar circuit sits north of
            // both Kitchen circuits, the block order (Kitchen first) wins over raw geometry across zones.
            var loops = new[] { Loop("L1", "Kitchen", "Bar") };
            var units = new[]
            {
                U("k1", "Kitchen", 0, 0), U("k2", "Kitchen", 0, 5),
                U("b1", "Bar", 0, 100),
            };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "k2"));  // Kitchen block, NW-first (k2 north of k1)
            Assert.Equal("L1-01", Addr(a, "k1"));
            Assert.Equal("L1-02", Addr(a, "b1"));  // Bar block, after Kitchen despite being furthest north
        }

        [Fact]
        public void Fresh_TwoLoops_NumberedInDeclaredOrder()
        {
            var loops = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var units = new[] { U("k", "Kitchen", 0, 0), U("b", "Bath", 0, 0) };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "k"));
            Assert.Equal("L2-00", Addr(a, "b"));
        }

        [Fact]
        public void Fresh_UnzonedAndUnloopedUnits_GetNoAddress()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var units = new[]
            {
                U("k", "Kitchen", 0, 0),
                U("blank", "", 0, 0),        // no zone ⇒ unaddressable
                U("pool", "Foyer", 0, 0),    // zone exists but is in no loop ⇒ unaddressed
            };

            var a = Fresh(loops, units);

            Assert.True(a.TextByUnit.ContainsKey("k"));
            Assert.False(a.TextByUnit.ContainsKey("blank"));
            Assert.False(a.TextByUnit.ContainsKey("pool"));
        }

        [Fact]
        public void Fresh_PointlessUnits_AppendAfterLocated()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var units = new[] { U("c1", "Kitchen", 0, 0), Unp("z", "Kitchen"), Unp("m", "Kitchen") };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "c1"));   // located first
            Assert.Equal("L1-01", Addr(a, "m"));    // then point-less by ordinal key
            Assert.Equal("L1-02", Addr(a, "z"));
        }

        // ── Driver units: N-per-circuit expansion + uniform mixed walk ─────────────────────────────────────

        [Fact]
        public void Fresh_DriverCircuit_ExpandsToNContiguousAddresses_InOrdinalOrder()
        {
            // One circuit, three drivers (ordinals presented out of order) ⇒ three contiguous addresses in
            // ordinal (down-column) order, zero-based.
            var loops = new[] { Loop("L1", "cove") };
            var units = new[]
            {
                Drv("cA", 2, "cove", 0, 0), Drv("cA", 0, "cove", 0, 0), Drv("cA", 1, "cove", 0, 0),
            };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "cA#0"));
            Assert.Equal("L1-01", Addr(a, "cA#1"));
            Assert.Equal("L1-02", Addr(a, "cA#2"));
        }

        [Fact]
        public void Fresh_MixedZone_IsOneUniformWalk_NotDownlightsFirst()
        {
            // A zone mixing a driver circuit (2 drivers) and a downlight, ordered purely by circuit centroid:
            // the driver circuit sits north, so its drivers take the LOW addresses ahead of the downlight —
            // proving the order is the spatial walk, not a "downlights-first" block rule.
            var loops = new[] { Loop("L1", "Living") };
            var units = new[]
            {
                U("d1", "Living", 0, 0),                                    // downlight, south
                Drv("cA", 0, "Living", 0, 10), Drv("cA", 1, "Living", 0, 10), // driver circuit, north
            };

            var a = Fresh(loops, units);

            Assert.Equal("L1-00", Addr(a, "cA#0"));   // driver circuit is north ⇒ first
            Assert.Equal("L1-01", Addr(a, "cA#1"));
            Assert.Equal("L1-02", Addr(a, "d1"));     // downlight after
        }

        [Fact]
        public void Locked_DriverRedeploy_KeepsAddresses_NoReview()
        {
            // A driver key is circuit.UniqueId#ordinal, so a redeploy that keeps the same circuit + ordinals
            // (fresh element instances, same keys) pins every address — no gap, no append, no REVIEW.
            var loops = new[] { Loop("L1", "cove") };
            var units = new[] { Drv("cA", 0, "cove", 0, 0), Drv("cA", 1, "cove", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, units));

            var a = Locked(loops, units, baseline);

            Assert.Equal("L1-00", Addr(a, "cA#0"));
            Assert.Equal("L1-01", Addr(a, "cA#1"));
            Assert.False(a.HasReviews);
        }

        // ── Pinned (locked) ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Locked_Unchanged_KeepsIdenticalAddresses()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var units = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, units));

            var a = Locked(loops, units, baseline);

            Assert.Equal("L1-00", Addr(a, "c2"));
            Assert.Equal("L1-01", Addr(a, "c1"));
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_UnitAdded_AppendsPastHighWater_NoReview()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var start = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));

            // c3 added north of everything — but locked ⇒ it appends past the loop high-water (01), not into
            // the tidy NW slot it would take unlocked.
            var grown = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10), U("c3", "Kitchen", 0, 20) };
            var a = Locked(loops, grown, baseline);

            Assert.Equal("L1-00", Addr(a, "c2"));   // pinned
            Assert.Equal("L1-01", Addr(a, "c1"));   // pinned
            Assert.Equal("L1-02", Addr(a, "c3"));   // appended
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_UnitDeleted_SlotGaps_AndReviews()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var start = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10) };  // c2=00, c1=01
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));

            var afterDelete = new[] { U("c1", "Kitchen", 0, 0) };   // c2 removed
            var a = Locked(loops, afterDelete, baseline);

            Assert.Equal("L1-01", Addr(a, "c1"));                   // kept its slot; 00 is now a gap
            Assert.False(a.TextByUnit.ContainsKey("c2"));
            var review = Assert.Single(a.Reviews);
            Assert.Equal("c2", review.UnitKey);
            Assert.Contains("deleted", review.Message);
            Assert.Contains("L1-00", review.Message);
        }

        [Fact]
        public void Locked_NewLoop_AppendsPastLoopHighWater_NoReview()
        {
            var loopsStart = new[] { Loop("La", "Kitchen") };
            var units0 = new[] { U("k", "Kitchen", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loopsStart, units0));

            var loops = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var units = new[] { U("k", "Kitchen", 0, 0), U("b", "Bath", 0, 0) };
            var a = Locked(loops, units, baseline);

            Assert.Equal("L1-00", Addr(a, "k"));   // kept
            Assert.Equal("L2-00", Addr(a, "b"));   // new loop appended as L2
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_DeletedLoopNumber_IsNotReused()
        {
            // Baseline has L1(Kitchen)=1 and L2(Bath)=2. Delete Bath's loop, add a brand-new Foyer loop:
            // it must append as L3, never refilling the retired L2.
            var loops0 = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var units0 = new[] { U("k", "Kitchen", 0, 0), U("b", "Bath", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, units0));

            var loops1 = new[] { Loop("La", "Kitchen"), Loop("Lc", "Foyer") };
            var units1 = new[] { U("k", "Kitchen", 0, 0), U("f", "Foyer", 0, 0) };
            var a = Locked(loops1, units1, baseline);

            Assert.Equal("L1-00", Addr(a, "k"));
            Assert.Equal("L3-00", Addr(a, "f"));   // L2 stays a gap, not reused
        }

        [Fact]
        public void Locked_UnitMovedLoops_IsReviewedAndReissued()
        {
            var loops0 = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var units0 = new[] { U("k", "Kitchen", 0, 0), U("b", "Bath", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, units0));  // b = L2-00

            // Move unit b's zone (Bath) into loop La by declaring Bath under La.
            var loops1 = new[] { Loop("La", "Kitchen", "Bath"), Loop("Lb") };
            var units1 = new[] { U("k", "Kitchen", 0, 0), U("b", "Bath", 0, 0) };
            var a = Locked(loops1, units1, baseline);

            var review = Assert.Single(a.Reviews);
            Assert.Equal("b", review.UnitKey);
            Assert.Contains("moved", review.Message);
            Assert.StartsWith("L1-", Addr(a, "b"));   // re-issued onto its new loop (La = L1)
        }

        [Fact]
        public void Locked_UnitMovedZonesWithinSameLoop_IsSilent()
        {
            // Loop La groups both Kitchen and Bar. A unit that moves Kitchen→Bar stays on La (L1), so its
            // L# is still correct ⇒ no REVIEW (mirrors DMX not flagging a same-count type swap). Its slot is
            // pinned, so the address value is unchanged.
            var loops = new[] { Loop("La", "Kitchen", "Bar") };
            var start = new[] { U("m", "Kitchen", 0, 0), U("x", "Bar", 0, 5) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));
            string issued = Addr(Fresh(loops, start), "m");

            var moved = new[] { U("m", "Bar", 0, 0), U("x", "Bar", 0, 5) };  // m: Kitchen → Bar
            var a = Locked(loops, moved, baseline);

            Assert.False(a.HasReviews);
            Assert.Equal(issued, Addr(a, "m"));   // slot pinned, still same loop
        }

        [Fact]
        public void Locked_UnitUnassignedFromAnyLoop_ReviewsAsRetired_NotDeleted()
        {
            var loops0 = new[] { Loop("La", "Kitchen") };
            var units = new[] { U("k", "Kitchen", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, units));

            // Unit k still exists, but its zone is no longer in any loop (returned to the pool).
            var loops1 = System.Array.Empty<DaliLoopInput>();
            var a = Locked(loops1, units, baseline);

            var review = Assert.Single(a.Reviews);
            Assert.Contains("no longer on an addressed loop", review.Message);
            Assert.DoesNotContain("deleted", review.Message);
        }

        [Fact]
        public void LockBaseline_IsStableAcrossSuccessiveLockedRuns()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var units = new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, units));

            var run1 = Locked(loops, new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10),
                                             U("c3", "Kitchen", 0, 20) }, baseline);
            // c4 added SOUTH of everything ⇒ later in the NW walk, so it appends AFTER c3 without moving it.
            var run2 = Locked(loops, new[] { U("c1", "Kitchen", 0, 0), U("c2", "Kitchen", 0, 10),
                                             U("c3", "Kitchen", 0, 20), U("c4", "Kitchen", 0, -10) }, baseline);

            Assert.Equal("L1-02", Addr(run1, "c3"));
            Assert.Equal("L1-02", Addr(run2, "c3"));   // earlier append didn't move
            Assert.Equal("L1-03", Addr(run2, "c4"));   // next append lands after
        }
    }
}
