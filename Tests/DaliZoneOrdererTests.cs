using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliZoneOrderer"/> (the single within-zone circuit ordering shared by the
    /// reconciler and the DALI tab) plus end-to-end <see cref="DaliAddressReconciler"/> cases that exercise the
    /// room-order Auto sort and the per-zone Manual override. Pure — synthetic circuits/readings, no Revit.
    /// </summary>
    public class DaliZoneOrdererTests
    {
        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

        private static ZoneCircuit C(string key, string room, double x, double y) =>
            new ZoneCircuit(key, room, new DaliPoint(x, y));

        private static IReadOnlyDictionary<string, int> Rank(params string[] rooms)
        {
            var d = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rooms.Length; i++) d[rooms[i]] = i;
            return d;
        }

        private static readonly IReadOnlyDictionary<string, int> NoRank =
            new Dictionary<string, int>();

        private static IReadOnlyList<string> Order(
            IEnumerable<ZoneCircuit> circuits, IReadOnlyDictionary<string, int> rank, DaliZoneSort sort) =>
            DaliZoneOrderer.Order(circuits.ToList(), rank, sort);

        // ── Auto ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Auto_NoRoomOrder_IsOneNwWalk_OverAllCircuits()
        {
            // Three circuits in TWO rooms, no room order. Grouping by RANK (not room name) must collapse them
            // into a single NW walk — north-first — NOT split into per-room walks. This is the "unchanged when
            // no room order" invariant.
            var circuits = new[]
            {
                C("a", "RoomX", 0, 0),
                C("b", "RoomY", 0, 5),
                C("c", "RoomX", 0, 10),
            };

            var order = Order(circuits, NoRank, DaliZoneSort.Auto);

            Assert.Equal(new[] { "c", "b", "a" }, order);   // pure spatial, rooms interleaved
        }

        [Fact]
        public void Auto_RoomOrder_GroupsByRank_ThenSpatialWithinRoom()
        {
            // Room order [Kitchen, Dining]. Kitchen circuits walk together (spatially) ahead of Dining,
            // regardless of raw geometry across rooms.
            var circuits = new[]
            {
                C("d1", "Dining", 0, 100),    // far north, but Dining is ranked last
                C("k1", "Kitchen", 0, 0),
                C("k2", "Kitchen", 0, 10),
            };

            var order = Order(circuits, Rank("Kitchen", "Dining"), DaliZoneSort.Auto);

            Assert.Equal(new[] { "k2", "k1", "d1" }, order);   // Kitchen (NW-first) then Dining
        }

        [Fact]
        public void Auto_UnrankedRooms_SinkLast_SpatialAmongThemselves()
        {
            // Kitchen is ranked; the two Attic circuits are not ⇒ they trail, walked among themselves.
            var circuits = new[]
            {
                C("att1", "Attic", 0, 0),
                C("att2", "Attic", 0, 20),
                C("k1", "Kitchen", 0, 5),
            };

            var order = Order(circuits, Rank("Kitchen"), DaliZoneSort.Auto);

            Assert.Equal(new[] { "k1", "att2", "att1" }, order);   // Kitchen first, then Attic NW-first
        }

        [Fact]
        public void Auto_TwoCircuitsSameRoom_StayTogether_EvenIfAnotherRoomIsBetween()
        {
            // Room order [RoomA, RoomB]. A RoomB circuit sits geometrically between the two RoomA circuits, but
            // rank grouping keeps both RoomA circuits contiguous ahead of RoomB (the Screenshot_646 fix shape).
            var circuits = new[]
            {
                C("a1", "RoomA", 0, 0),
                C("b1", "RoomB", 0, 5),
                C("a2", "RoomA", 0, 10),
            };

            var order = Order(circuits, Rank("RoomA", "RoomB"), DaliZoneSort.Auto);

            Assert.Equal(new[] { "a2", "a1", "b1" }, order);   // both RoomA (NW-first) then RoomB
        }

        // ── Manual ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Manual_HonorsDraggedOrder_IgnoringRoomRankAndGeometry()
        {
            var circuits = new[]
            {
                C("c1", "Kitchen", 0, 0),
                C("c2", "Kitchen", 0, 10),
                C("c3", "Kitchen", 0, 20),
            };
            var sort = new DaliZoneSort(true, new[] { "c3", "c1", "c2" });

            var order = Order(circuits, Rank("Kitchen"), sort);

            Assert.Equal(new[] { "c3", "c1", "c2" }, order);
        }

        [Fact]
        public void Manual_NewCircuitNotInSavedOrder_IsAppendedInAutoOrder()
        {
            // Saved manual order is [c3, c1]; c2 was added since the drag. It must append at the end (not
            // disappear), in Auto order among the leftovers.
            var circuits = new[]
            {
                C("c1", "Kitchen", 0, 0),
                C("c2", "Kitchen", 0, 10),
                C("c3", "Kitchen", 0, 20),
            };
            var sort = new DaliZoneSort(true, new[] { "c3", "c1" });

            var order = Order(circuits, NoRank, sort);

            Assert.Equal(new[] { "c3", "c1", "c2" }, order);
        }

        [Fact]
        public void Manual_StaleKeyInSavedOrder_IsSkipped()
        {
            // Saved order names c9, which no longer exists ⇒ skipped; the live circuits still all appear.
            var circuits = new[] { C("c1", "Kitchen", 0, 0), C("c2", "Kitchen", 0, 10) };
            var sort = new DaliZoneSort(true, new[] { "c9", "c2", "c1" });

            var order = Order(circuits, NoRank, sort);

            Assert.Equal(new[] { "c2", "c1" }, order);
        }

        // ── End-to-end through the reconciler ──────────────────────────────────────────────────────────────

        private static DaliLoopInput Loop(string id, params string[] zones) =>
            new DaliLoopInput(id, id, zones);

        private static DaliLoopInput LoopWithSort(
            string id, string zone, DaliZoneSort sort, params string[] zones) =>
            new DaliLoopInput(id, id, zones,
                new Dictionary<string, DaliZoneSort>(System.StringComparer.OrdinalIgnoreCase) { [zone] = sort });

        // A driver unit on `circuit` at ordinal `ord`, sharing its circuit's room + centroid.
        private static DaliUnitReading Drv(string circuit, int ord, string zone, string room, double x, double y) =>
            new DaliUnitReading(circuit + "#" + ord, circuit, DaliUnitKind.Driver, ord, zone,
                                new DaliPoint(x, y), room);

        private static string Addr(DaliAddressing a, string key) => a.TextByUnit[key];

        [Fact]
        public void Reconcile_RoomOrder_ReordersDriverCircuitsAcrossRooms()
        {
            // One loop, one zone "cove", two single-driver circuits in different rooms. Geometry alone (NW
            // walk) would put the north circuit first; the room order [South, North] flips that.
            var loops = new[] { Loop("L1", "cove") };
            var units = new[]
            {
                Drv("cN", 0, "cove", "North", 0, 100),   // north
                Drv("cS", 0, "cove", "South", 0, 0),     // south
            };

            var a = DaliAddressReconciler.Reconcile(
                loops, units, null, false, new[] { "South", "North" });

            Assert.Equal("L1-00", Addr(a, "cS#0"));   // South ranked first, despite being south
            Assert.Equal("L1-01", Addr(a, "cN#0"));
        }

        [Fact]
        public void Reconcile_NoRoomOrder_MatchesPureSpatial()
        {
            // Same units, no room order ⇒ the north circuit leads (pure NW walk) — the pre-feature behavior.
            var loops = new[] { Loop("L1", "cove") };
            var units = new[]
            {
                Drv("cN", 0, "cove", "North", 0, 100),
                Drv("cS", 0, "cove", "South", 0, 0),
            };

            var a = DaliAddressReconciler.Reconcile(loops, units, null, false, roomOrder: null);

            Assert.Equal("L1-00", Addr(a, "cN#0"));   // north-first, geometry only
            Assert.Equal("L1-01", Addr(a, "cS#0"));
        }

        [Fact]
        public void Reconcile_Manual_HonorsZoneSort()
        {
            var sort = new DaliZoneSort(true, new[] { "cS", "cN" });
            var loops = new[] { LoopWithSort("L1", "cove", sort, "cove") };
            var units = new[]
            {
                Drv("cN", 0, "cove", "North", 0, 100),
                Drv("cS", 0, "cove", "South", 0, 0),
            };

            var a = DaliAddressReconciler.Reconcile(loops, units, null, false, roomOrder: null);

            Assert.Equal("L1-00", Addr(a, "cS#0"));   // manual order wins over geometry
            Assert.Equal("L1-01", Addr(a, "cN#0"));
        }

        [Fact]
        public void Reconcile_Locked_SortChange_DoesNotMoveIssuedAddresses_OnlyNewAppendsFollow()
        {
            // Lock a two-circuit zone under Auto (no room order): cN=00, cS=01. Then flip the zone to a Manual
            // order that reverses them AND add a brand-new circuit. The issued pair must NOT move; only the new
            // circuit appends — and it appends past the loop high-water, following the new order among appendees.
            var loops0 = new[] { Loop("L1", "cove") };
            var units0 = new[]
            {
                Drv("cN", 0, "cove", "North", 0, 100),
                Drv("cS", 0, "cove", "South", 0, 0),
            };
            var fresh = DaliAddressReconciler.Reconcile(loops0, units0, null, false);
            var baseline = DaliSnapshotBuilder.Capture(fresh);
            Assert.Equal("L1-00", Addr(fresh, "cN#0"));
            Assert.Equal("L1-01", Addr(fresh, "cS#0"));

            var sort = new DaliZoneSort(true, new[] { "cS", "cN", "cNew" });
            var loops1 = new[] { LoopWithSort("L1", "cove", sort, "cove") };
            var units1 = new[]
            {
                Drv("cN", 0, "cove", "North", 0, 100),
                Drv("cS", 0, "cove", "South", 0, 0),
                Drv("cNew", 0, "cove", "North", 0, 50),
            };

            var a = DaliAddressReconciler.Reconcile(loops1, units1, baseline, true);

            Assert.Equal("L1-00", Addr(a, "cN#0"));    // issued addresses pinned, unchanged by the sort flip
            Assert.Equal("L1-01", Addr(a, "cS#0"));
            Assert.Equal("L1-02", Addr(a, "cNew#0"));  // only the new circuit appends
            Assert.False(a.HasReviews);
        }
    }
}
