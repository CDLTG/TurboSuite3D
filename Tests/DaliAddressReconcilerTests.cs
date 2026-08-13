using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliAddressReconciler"/> — the two-level (loop L# + load ##) lock-aware
    /// addressing reconcile, the DALI analog of <see cref="TurboSuite.Tests.Dmx.DmxLockReconcilerTests"/>.
    /// Covers the Fresh path (zone-block → NW-seeded walk, contiguous L#) and the Pinned path (kept slots,
    /// gap-safe append, deleted/moved REVIEWs, silent within-loop zone move), plus lock-baseline stability.
    /// Pure — synthetic loops + circuit centroids, no Revit.
    /// </summary>
    public class DaliAddressReconcilerTests
    {
        private static DaliLoopInput Loop(string id, params string[] zones) =>
            new DaliLoopInput(id, id, zones);

        private static DaliCircuitReading C(string key, string zone, double x, double y) =>
            new DaliCircuitReading(key, zone, new DaliPoint(x, y));

        private static DaliCircuitReading Cnp(string key, string zone) =>
            new DaliCircuitReading(key, zone, null);

        private static DaliAddressing Fresh(IEnumerable<DaliLoopInput> loops, IEnumerable<DaliCircuitReading> ckts) =>
            DaliAddressReconciler.Reconcile(loops.ToList(), ckts.ToList(), null, false);

        private static DaliAddressing Locked(
            IEnumerable<DaliLoopInput> loops, IEnumerable<DaliCircuitReading> ckts, DaliSnapshotDto baseline) =>
            DaliAddressReconciler.Reconcile(loops.ToList(), ckts.ToList(), baseline, true);

        private static string Addr(DaliAddressing a, string key) => a.TextByCircuit[key];

        // ── Fresh (unlocked) ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Fresh_SingleZone_NumbersInNwWalkOrder()
        {
            // c2 sits north of c1 ⇒ NW seed gives c2 = L1-01, c1 = L1-02.
            var loops = new[] { Loop("L1", "Kitchen") };
            var ckts = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };

            var a = Fresh(loops, ckts);

            Assert.Equal("L1-01", Addr(a, "c2"));
            Assert.Equal("L1-02", Addr(a, "c1"));
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Fresh_ZonesAreContiguousBlocks_InDeclaredOrder()
        {
            // Loop groups [Kitchen, Bar] in that declared order. Even though the Bar circuit sits north of
            // both Kitchen circuits, the block order (Kitchen first) wins over raw geometry across zones.
            var loops = new[] { Loop("L1", "Kitchen", "Bar") };
            var ckts = new[]
            {
                C("k1", "Kitchen", 0, 0), C("k2", "Kitchen", 0, 5),
                C("b1", "Bar", 0, 100),
            };

            var a = Fresh(loops, ckts);

            Assert.Equal("L1-01", Addr(a, "k2"));  // Kitchen block, NW-first (k2 north of k1)
            Assert.Equal("L1-02", Addr(a, "k1"));
            Assert.Equal("L1-03", Addr(a, "b1"));  // Bar block, after Kitchen despite being furthest north
        }

        [Fact]
        public void Fresh_TwoLoops_NumberedInDeclaredOrder()
        {
            var loops = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var ckts = new[] { C("k", "Kitchen", 0, 0), C("b", "Bath", 0, 0) };

            var a = Fresh(loops, ckts);

            Assert.Equal("L1-01", Addr(a, "k"));
            Assert.Equal("L2-01", Addr(a, "b"));
        }

        [Fact]
        public void Fresh_UnzonedAndUnloopedCircuits_GetNoAddress()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var ckts = new[]
            {
                C("k", "Kitchen", 0, 0),
                C("blank", "", 0, 0),        // no zone ⇒ unaddressable
                C("pool", "Foyer", 0, 0),    // zone exists but is in no loop ⇒ unaddressed
            };

            var a = Fresh(loops, ckts);

            Assert.True(a.TextByCircuit.ContainsKey("k"));
            Assert.False(a.TextByCircuit.ContainsKey("blank"));
            Assert.False(a.TextByCircuit.ContainsKey("pool"));
        }

        [Fact]
        public void Fresh_PointlessCircuits_AppendAfterLocated()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var ckts = new[] { C("c1", "Kitchen", 0, 0), Cnp("z", "Kitchen"), Cnp("m", "Kitchen") };

            var a = Fresh(loops, ckts);

            Assert.Equal("L1-01", Addr(a, "c1"));   // located first
            Assert.Equal("L1-02", Addr(a, "m"));    // then point-less by ordinal key
            Assert.Equal("L1-03", Addr(a, "z"));
        }

        // ── Pinned (locked) ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Locked_Unchanged_KeepsIdenticalAddresses()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var ckts = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, ckts));

            var a = Locked(loops, ckts, baseline);

            Assert.Equal("L1-01", Addr(a, "c2"));
            Assert.Equal("L1-02", Addr(a, "c1"));
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_CircuitAdded_AppendsPastHighWater_NoReview()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var start = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));

            // c3 added north of everything — but locked ⇒ it appends past the loop high-water (02), not into
            // the tidy NW slot it would take unlocked.
            var grown = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10), C("c3", "Kitchen", 0, 20) };
            var a = Locked(loops, grown, baseline);

            Assert.Equal("L1-01", Addr(a, "c2"));   // pinned
            Assert.Equal("L1-02", Addr(a, "c1"));   // pinned
            Assert.Equal("L1-03", Addr(a, "c3"));   // appended
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_CircuitDeleted_SlotGaps_AndReviews()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var start = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };  // c2=01, c1=02
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));

            var afterDelete = new[] { C("c1", "Kitchen", 0, 0) };   // c2 removed
            var a = Locked(loops, afterDelete, baseline);

            Assert.Equal("L1-02", Addr(a, "c1"));                   // kept its slot; 01 is now a gap
            Assert.False(a.TextByCircuit.ContainsKey("c2"));
            var review = Assert.Single(a.Reviews);
            Assert.Equal("c2", review.CircuitKey);
            Assert.Contains("deleted", review.Message);
            Assert.Contains("L1-01", review.Message);
        }

        [Fact]
        public void Locked_NewLoop_AppendsPastLoopHighWater_NoReview()
        {
            var loopsStart = new[] { Loop("La", "Kitchen") };
            var ckts0 = new[] { C("k", "Kitchen", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loopsStart, ckts0));

            var loops = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var ckts = new[] { C("k", "Kitchen", 0, 0), C("b", "Bath", 0, 0) };
            var a = Locked(loops, ckts, baseline);

            Assert.Equal("L1-01", Addr(a, "k"));   // kept
            Assert.Equal("L2-01", Addr(a, "b"));   // new loop appended as L2
            Assert.False(a.HasReviews);
        }

        [Fact]
        public void Locked_DeletedLoopNumber_IsNotReused()
        {
            // Baseline has L1(Kitchen)=1 and L2(Bath)=2. Delete Bath's loop, add a brand-new Foyer loop:
            // it must append as L3, never refilling the retired L2.
            var loops0 = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var ckts0 = new[] { C("k", "Kitchen", 0, 0), C("b", "Bath", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, ckts0));

            var loops1 = new[] { Loop("La", "Kitchen"), Loop("Lc", "Foyer") };
            var ckts1 = new[] { C("k", "Kitchen", 0, 0), C("f", "Foyer", 0, 0) };
            var a = Locked(loops1, ckts1, baseline);

            Assert.Equal("L1-01", Addr(a, "k"));
            Assert.Equal("L3-01", Addr(a, "f"));   // L2 stays a gap, not reused
        }

        [Fact]
        public void Locked_CircuitMovedLoops_IsReviewedAndReissued()
        {
            var loops0 = new[] { Loop("La", "Kitchen"), Loop("Lb", "Bath") };
            var ckts0 = new[] { C("k", "Kitchen", 0, 0), C("b", "Bath", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, ckts0));  // b = L2-01

            // Move circuit b's zone (Bath) into loop La by declaring Bath under La.
            var loops1 = new[] { Loop("La", "Kitchen", "Bath"), Loop("Lb") };
            var ckts1 = new[] { C("k", "Kitchen", 0, 0), C("b", "Bath", 0, 0) };
            var a = Locked(loops1, ckts1, baseline);

            var review = Assert.Single(a.Reviews);
            Assert.Equal("b", review.CircuitKey);
            Assert.Contains("moved", review.Message);
            Assert.StartsWith("L1-", Addr(a, "b"));   // re-issued onto its new loop (La = L1)
        }

        [Fact]
        public void Locked_CircuitMovedZonesWithinSameLoop_IsSilent()
        {
            // Loop La groups both Kitchen and Bar. A circuit that moves Kitchen→Bar stays on La (L1), so its
            // L# is still correct ⇒ no REVIEW (mirrors DMX not flagging a same-count type swap). Its slot is
            // pinned, so the address value is unchanged.
            var loops = new[] { Loop("La", "Kitchen", "Bar") };
            var start = new[] { C("m", "Kitchen", 0, 0), C("x", "Bar", 0, 5) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, start));
            string issued = Addr(Fresh(loops, start), "m");

            var moved = new[] { C("m", "Bar", 0, 0), C("x", "Bar", 0, 5) };  // m: Kitchen → Bar
            var a = Locked(loops, moved, baseline);

            Assert.False(a.HasReviews);
            Assert.Equal(issued, Addr(a, "m"));   // slot pinned, still same loop
        }

        [Fact]
        public void Locked_CircuitUnassignedFromAnyLoop_ReviewsAsRetired_NotDeleted()
        {
            var loops0 = new[] { Loop("La", "Kitchen") };
            var ckts = new[] { C("k", "Kitchen", 0, 0) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops0, ckts));

            // Circuit k still exists, but its zone is no longer in any loop (returned to the pool).
            var loops1 = System.Array.Empty<DaliLoopInput>();
            var a = Locked(loops1, ckts, baseline);

            var review = Assert.Single(a.Reviews);
            Assert.Contains("no longer on an addressed loop", review.Message);
            Assert.DoesNotContain("deleted", review.Message);
        }

        [Fact]
        public void LockBaseline_IsStableAcrossSuccessiveLockedRuns()
        {
            var loops = new[] { Loop("L1", "Kitchen") };
            var ckts = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };
            var baseline = DaliSnapshotBuilder.Capture(Fresh(loops, ckts));

            var run1 = Locked(loops, new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10),
                                             C("c3", "Kitchen", 0, 20) }, baseline);
            // c4 added SOUTH of everything ⇒ later in the NW walk, so it appends AFTER c3 without moving it.
            var run2 = Locked(loops, new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10),
                                             C("c3", "Kitchen", 0, 20), C("c4", "Kitchen", 0, -10) }, baseline);

            Assert.Equal("L1-03", Addr(run1, "c3"));
            Assert.Equal("L1-03", Addr(run2, "c3"));   // earlier append didn't move
            Assert.Equal("L1-04", Addr(run2, "c4"));   // next append lands after
        }
    }
}
