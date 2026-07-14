using System.Collections.Generic;
using System.Linq;
using TurboSuite.Name.Regions;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for the Revit-free TurboName region engine (Core/Name/). The Shim adapter
    /// (RegionWatershedService) is validated by manual in-Revit testing; everything below is pure.
    /// </summary>
    public class RegionWatershedTests
    {
        // ── GapBridging ──

        [Fact]
        public void GapBridging_JoinsLooseEndsWithinAFoot()
        {
            // Two colinear-ish segments whose near ends sit 0.5 ft apart (a corner drafting break).
            var walls = new List<WallSeg>
            {
                new WallSeg(new Pt(0, 0), new Pt(5, 0)),
                new WallSeg(new Pt(5.5, 0), new Pt(10, 0)),
            };
            GapBridging.BridgeProximityGaps(walls, out var bridges, out _);
            Assert.Single(bridges);
            Assert.Equal(0.5, (bridges[0].End - bridges[0].Start).GetLength(), 3);
        }

        [Fact]
        public void GapBridging_IgnoresGapsWiderThanAFoot()
        {
            var walls = new List<WallSeg>
            {
                new WallSeg(new Pt(0, 0), new Pt(5, 0)),
                new WallSeg(new Pt(7, 0), new Pt(12, 0)), // 2 ft gap — a real opening, not a corner break
            };
            GapBridging.BridgeProximityGaps(walls, out var bridges, out _);
            Assert.Empty(bridges);
        }

        [Fact]
        public void GapBridging_LeavesAlreadyConnectedEndsAlone()
        {
            var walls = new List<WallSeg>
            {
                new WallSeg(new Pt(0, 0), new Pt(5, 0)),
                new WallSeg(new Pt(5, 0), new Pt(5, 5)), // shares the (5,0) corner — already connected
            };
            GapBridging.BridgeProximityGaps(walls, out var bridges, out _);
            Assert.Empty(bridges);
        }

        // ── RegionVectorizer ──

        [Fact]
        public void Vectorizer_TracesASolidBlockToItsRectangle()
        {
            // 12×12 grid, owner 2 fills the interior block x∈[2,7], y∈[2,7].
            const int w = 12, h = 12, owner = 2;
            var grid = new int[w * h];
            long count = 0;
            for (int y = 2; y <= 7; y++)
                for (int x = 2; x <= 7; x++) { grid[y * w + x] = owner; count++; }

            // toModel = identity (1 px ⇒ 1 ft here); no walls to snap to.
            var poly = RegionVectorizer.Trace(grid, w, h, owner, count,
                (px, py) => new Pt(px, py), new List<WallSeg>(), out string why);

            Assert.Null(why);
            Assert.NotNull(poly);
            Assert.True(poly.Count >= 4, $"expected ≥4 corners, got {poly.Count}");
            Assert.Equal(2, poly.Min(p => p.X), 1);
            Assert.Equal(7, poly.Max(p => p.X), 1);
            Assert.Equal(2, poly.Min(p => p.Y), 1);
            Assert.Equal(7, poly.Max(p => p.Y), 1);
        }

        [Fact]
        public void Vectorizer_ReturnsNullWithReasonForAbsentOwner()
        {
            var grid = new int[100]; // all Free — owner 5 does not exist
            var poly = RegionVectorizer.Trace(grid, 10, 10, owner: 5, pixelCount: 0,
                (px, py) => new Pt(px, py), new List<WallSeg>(), out string why);
            Assert.Null(poly);
            Assert.False(string.IsNullOrEmpty(why));
        }

        // ── RegionWatershedEngine (end-to-end partition) ──

        [Fact]
        public void Engine_PartitionsTwoRoomsSplitByAWalledDoor()
        {
            // A 20×10 ft building: rectangular envelope, an interior wall at x=10 with a 2 ft door gap
            // (y 4→6). Two seeds. Expect a clean 2-way partition, no leaks.
            var envelope = Rect(0, 0, 20, 10);
            var walls = new List<WallSeg>
            {
                new WallSeg(new Pt(10, 0), new Pt(10, 4)),  // interior wall, lower run
                new WallSeg(new Pt(10, 6), new Pt(10, 10)), // interior wall, upper run (gap 4→6 = the door)
            };
            var doors = new List<Pt> { new Pt(10, 5) };
            var seeds = new List<Seed>
            {
                new Seed(new Pt(5, 5), "LEFT"),
                new Seed(new Pt(15, 5), "RIGHT"),
            };

            var output = RegionWatershedEngine.Run(walls, doors, envelope, seeds);

            Assert.Equal(2, output.Regions.Count);
            Assert.Equal(new[] { "LEFT", "RIGHT" },
                output.Regions.Select(r => r.RoomName).OrderBy(n => n).ToArray());
            Assert.All(output.Regions, r => Assert.True(r.Boundary.Count >= 4));
            Assert.Contains("Rooms partitioned: 2/2 seeded", output.Report);
            Assert.Contains("Leaks (>3000 sqft): 0", output.Report);
            Assert.NotNull(output.Grid); // grid returned for the debug PNG
        }

        [Fact]
        public void Engine_ReturnsEmptyWhenNoSeeds()
        {
            var output = RegionWatershedEngine.Run(
                Rect(0, 0, 10, 10), new List<Pt>(), Rect(0, 0, 10, 10), new List<Seed>());
            Assert.Empty(output.Regions);
            Assert.Null(output.Grid);
        }

        // Four wall segments forming a closed axis-aligned rectangle loop.
        private static List<WallSeg> Rect(double x0, double y0, double x1, double y1) => new List<WallSeg>
        {
            new WallSeg(new Pt(x0, y0), new Pt(x1, y0)),
            new WallSeg(new Pt(x1, y0), new Pt(x1, y1)),
            new WallSeg(new Pt(x1, y1), new Pt(x0, y1)),
            new WallSeg(new Pt(x0, y1), new Pt(x0, y0)),
        };
    }
}
