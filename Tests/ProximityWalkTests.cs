using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Addressing;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="ProximityWalk"/> — the NW-seeded proximity chain ported from TurboWire's
    /// <c>FixtureOrderingService</c>. Covers the seed corner (max Y, then min X), contiguity of a chain,
    /// determinism under input reordering, and the point-less fallback. Pure — synthetic point layouts.
    /// </summary>
    public class ProximityWalkTests
    {
        private static WalkNode N(string key, double x, double y) => new WalkNode(key, new DaliPoint(x, y));
        private static WalkNode NoPt(string key) => new WalkNode(key, null);

        private static List<string> Order(params WalkNode[] nodes) => ProximityWalk.NwSeededOrder(nodes);

        [Fact]
        public void Empty_ReturnsEmpty() => Assert.Empty(Order());

        [Fact]
        public void Single_ReturnsItself() => Assert.Equal(new[] { "a" }, Order(N("a", 5, 5)));

        [Fact]
        public void Two_HigherYComesFirst()
        {
            // b sits north of a ⇒ NW seed picks b first.
            Assert.Equal(new[] { "b", "a" }, Order(N("a", 0, 0), N("b", 0, 10)));
        }

        [Fact]
        public void Two_SameY_LowerXComesFirst()
        {
            // Equal Y ⇒ the westmost (min X) leads.
            Assert.Equal(new[] { "a", "b" }, Order(N("b", 10, 5), N("a", 0, 5)));
        }

        [Fact]
        public void VerticalLine_OrdersTopToBottom()
        {
            var order = Order(N("a", 0, 0), N("b", 0, 10), N("c", 0, 20));
            Assert.Equal(new[] { "c", "b", "a" }, order);   // NW = top (max Y) first, walk down
        }

        [Fact]
        public void HorizontalLine_OrdersWestToEast()
        {
            var order = Order(N("a", 0, 5), N("b", 10, 5), N("c", 20, 5));
            Assert.Equal(new[] { "a", "b", "c" }, order);   // equal Y ⇒ west (min X) first, walk east
        }

        [Fact]
        public void Chain_IsContiguousNeighborWalk_NotAxisSort()
        {
            // An L-shape: proximity keeps neighbors adjacent. Seed at the NW-most point (0,20).
            //   c(0,20) — b(0,0) — a(30,0)   is the natural chain from the NW corner.
            var order = Order(N("a", 30, 0), N("b", 0, 0), N("c", 0, 20));
            Assert.Equal(new[] { "c", "b", "a" }, order);
        }

        [Fact]
        public void Deterministic_UnderInputReordering()
        {
            var forward = Order(N("a", 0, 0), N("b", 5, 1), N("c", 10, 0), N("d", 15, 1));
            var shuffled = Order(N("d", 15, 1), N("a", 0, 0), N("c", 10, 0), N("b", 5, 1));
            Assert.Equal(forward, shuffled);
        }

        [Fact]
        public void PointlessNodes_AppendedAfterLocated_ByOrdinalKey()
        {
            var order = Order(N("a", 0, 10), N("b", 0, 0), NoPt("z"), NoPt("m"));
            // Located walk first (a north of b), then point-less nodes sorted by key.
            Assert.Equal(new[] { "a", "b", "m", "z" }, order);
        }

        [Fact]
        public void AllPointless_FallsBackToKeyOrder()
        {
            Assert.Equal(new[] { "a", "b", "c" }, Order(NoPt("c"), NoPt("a"), NoPt("b")));
        }

        [Fact]
        public void SeedsAtNorthWestMostEndpoint()
        {
            // The chain's diameter runs nw(0,10)–se(10,0); the walk enters at the NW-most END (nw, higher Y),
            // so -01 lands top-left and the numbers read down-and-right.
            var order = Order(N("se", 10, 0), N("m", 5, 5), N("nw", 0, 10));
            Assert.Equal(new[] { "nw", "m", "se" }, order);
        }
    }
}
