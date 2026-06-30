using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Lock;
using TurboSuite.Dmx.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxLockReconciler"/> — the Control-Zone-anchored numbering lock (§8c). Covers
    /// the Unlocked fresh-numbering path and the Locked behaviors: unchanged ⇒ identical #s, additive ⇒
    /// append past high-water, shrink ⇒ retire (gap, no renumber), new zone ⇒ append, and the type/interface
    /// drift REVIEW verdicts. Pure — no Revit, no bill needed (the reconciler works off DmxSolvedZone).
    /// </summary>
    public class DmxLockReconcilerTests
    {
        private static DmxSolvedZone Z(string value, int iface, string decoder, int count) =>
            new DmxSolvedZone(value, iface, decoder, count);

        private static DmxSnapshotDto Baseline(params (string Zone, int Iface, string Decoder, int[] Ids)[] zones) =>
            new DmxSnapshotDto
            {
                NumberingState = "Locked",
                Zones = zones.Select(z => new DmxSnapshotZoneDto
                {
                    ZoneValue = z.Zone, InterfaceNumber = z.Iface, DecoderType = z.Decoder, DecIds = z.Ids.ToList(),
                }).ToList(),
            };

        private static IReadOnlyList<int> Ids(DmxNumbering n, string zone) => n.DecIdsByZone[zone];

        [Fact]
        public void Unlocked_AssignsFreshOneToN()
        {
            var zones = new[] { Z("Z1", 1, "4ch", 2), Z("Z2", 1, "4ch", 3) };
            var n = DmxLockReconciler.Reconcile(zones, baseline: null, locked: false);

            Assert.Equal(new[] { 1, 2 }, Ids(n, "Z1"));
            Assert.Equal(new[] { 3, 4, 5 }, Ids(n, "Z2"));
            Assert.False(n.HasReviews);
        }

        [Fact]
        public void Locked_UnchangedDesign_KeepsIdenticalNumbers()
        {
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }), ("Z2", 1, "4ch", new[] { 3, 4, 5 }));
            var zones = new[] { Z("Z1", 1, "4ch", 2), Z("Z2", 1, "4ch", 3) };

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            Assert.Equal(new[] { 1, 2 }, Ids(n, "Z1"));
            Assert.Equal(new[] { 3, 4, 5 }, Ids(n, "Z2"));
            Assert.False(n.HasReviews);
        }

        [Fact]
        public void Locked_ZoneGrew_AppendsPastHighWater()
        {
            // Z1 had DEC 1,2; now needs 4 ⇒ keeps 1,2, appends 6,7 (high-water was 5). No REVIEW (additive).
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }), ("Z2", 1, "4ch", new[] { 3, 4, 5 }));
            var zones = new[] { Z("Z1", 1, "4ch", 4), Z("Z2", 1, "4ch", 3) };

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            Assert.Equal(new[] { 1, 2, 6, 7 }, Ids(n, "Z1"));
            Assert.Equal(new[] { 3, 4, 5 }, Ids(n, "Z2"));
            Assert.False(n.HasReviews);
        }

        [Fact]
        public void Locked_ZoneShrank_RetiresTrailingNumbersAsGaps()
        {
            // Z2 had 3,4,5; now needs 2 ⇒ keeps 3,4; DEC 5 retired (gap), nothing renumbered.
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }), ("Z2", 1, "4ch", new[] { 3, 4, 5 }));
            var zones = new[] { Z("Z1", 1, "4ch", 2), Z("Z2", 1, "4ch", 2) };

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            Assert.Equal(new[] { 1, 2 }, Ids(n, "Z1"));
            Assert.Equal(new[] { 3, 4 }, Ids(n, "Z2"));
            Assert.False(n.HasReviews);
        }

        [Fact]
        public void Locked_NewZone_AppendsAfterExistingExtras()
        {
            // Existing Z1 grows by 1 (appends 6); brand-new Z3 appends entirely AFTER that (7), so adding a
            // zone never shifts an already-appended number.
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }), ("Z2", 1, "4ch", new[] { 3, 4, 5 }));
            var zones = new[] { Z("Z1", 1, "4ch", 3), Z("Z2", 1, "4ch", 3), Z("Z3", 2, "4ch", 1) };

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            Assert.Equal(new[] { 1, 2, 6 }, Ids(n, "Z1"));
            Assert.Equal(new[] { 3, 4, 5 }, Ids(n, "Z2"));
            Assert.Equal(new[] { 7 }, Ids(n, "Z3"));
            Assert.False(n.HasReviews);
        }

        [Fact]
        public void Locked_DecoderTypeChange_SameCount_KeepsNumbersAndDoesNotReview()
        {
            // Decision 2026-06-30: a same-count decoder-type swap is NOT a numbering REVIEW. Numbers are
            // pinned by slot (moved nothing), so no address shifts; the model/BOM delta is TurboDocs/Counts'.
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }));
            var zones = new[] { Z("Z1", 1, "6ch", 2) };   // type 4ch → 6ch, SAME count (2)

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            Assert.Equal(new[] { 1, 2 }, Ids(n, "Z1"));   // numbers still pinned
            Assert.False(n.HasReviews);                   // type change alone no longer flags
        }

        [Fact]
        public void Locked_InterfaceMove_FlagsReview()
        {
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }));
            var zones = new[] { Z("Z1", 2, "4ch", 2) };   // interface 1 → 2

            var n = DmxLockReconciler.Reconcile(zones, baseline, locked: true);

            var review = Assert.Single(n.Reviews);
            Assert.Contains("interface", review.Message);
        }

        [Fact]
        public void AdditiveNumbersStableAcrossSuccessiveLockedRuns()
        {
            // Contractor scenario: lock, then add a zone twice; earlier additive #s must not move.
            var baseline = Baseline(("Z1", 1, "4ch", new[] { 1, 2 }));

            var run1 = DmxLockReconciler.Reconcile(
                new[] { Z("Z1", 1, "4ch", 3) }, baseline, locked: true);          // Z1 +1 ⇒ 1,2,3
            var run2 = DmxLockReconciler.Reconcile(
                new[] { Z("Z1", 1, "4ch", 3), Z("Z9", 1, "4ch", 1) }, baseline, locked: true); // add Z9

            Assert.Equal(new[] { 1, 2, 3 }, Ids(run1, "Z1"));
            Assert.Equal(new[] { 1, 2, 3 }, Ids(run2, "Z1")); // unchanged
            Assert.Equal(new[] { 4 }, Ids(run2, "Z9"));       // appends after
        }
    }
}
