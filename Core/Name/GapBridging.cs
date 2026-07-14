#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Name.Regions
{
    /// <summary>
    /// Closes tiny wall-endpoint gaps at corners with short virtual wall segments — "two loose wall ends almost
    /// touch, join them." Nearest-neighbour within 12 inches, no angle test; because it can only ever span ≤ 1 ft
    /// it physically cannot wall off an intentional opening (door/nook/alcove).
    ///
    /// Collinear bridging (≤15 ft gaps between near-parallel, near-collinear wall ends) and the old door/window
    /// opening pass were REMOVED: the priority-flood watershed self-cuts doorless room-to-room gaps at the throat,
    /// and door openings are handled by <c>RegionWatershedEngine.SealDoorsAlongWalls</c>. Collinear's long reach
    /// also sealed intentional openings it couldn't distinguish from drafting breaks (e.g. a fireplace nook mouth).
    /// </summary>
    public static class GapBridging
    {
        private const double ConnectionTolerance = 0.01;    // ft — endpoints closer than this are already connected
        private const double ProximityBridgeDistance = 1.0; // 12 inches — bridge unconditionally (corner cleanup)

        /// <summary>
        /// Returns short virtual bridge segments closing sub-12-inch gaps between otherwise-unconnected wall
        /// endpoints, plus a diagnostic string. <paramref name="wallSegments"/> is not modified.
        /// </summary>
        public static void BridgeProximityGaps(
            List<WallSeg> wallSegments, out List<WallSeg> proximityBridges, out string info)
        {
            proximityBridges = new List<WallSeg>();

            // Endpoints of every wall segment (with parent index so a segment can't bridge to itself).
            var endpoints = new List<(Pt Point, int SegmentIndex)>();
            for (int i = 0; i < wallSegments.Count; i++)
            {
                endpoints.Add((wallSegments[i].Start, i));
                endpoints.Add((wallSegments[i].End, i));
            }

            // Unconnected = no other segment's endpoint sits right on top of it.
            var unconnected = endpoints.Where(ep => !endpoints.Any(other =>
                other.SegmentIndex != ep.SegmentIndex &&
                ep.Point.DistanceTo(other.Point) < ConnectionTolerance)).ToList();

            var bridged = new HashSet<int>();
            for (int i = 0; i < unconnected.Count; i++)
            {
                if (bridged.Contains(i)) continue;
                var ep1 = unconnected[i];

                int bestIdx = -1;
                double bestDist = ProximityBridgeDistance;
                for (int j = i + 1; j < unconnected.Count; j++)
                {
                    if (bridged.Contains(j)) continue;
                    if (unconnected[j].SegmentIndex == ep1.SegmentIndex) continue;

                    double dist = ep1.Point.DistanceTo(unconnected[j].Point);
                    if (dist < bestDist && dist > 0.001) { bestDist = dist; bestIdx = j; }
                }

                if (bestIdx >= 0)
                {
                    proximityBridges.Add(new WallSeg(ep1.Point, unconnected[bestIdx].Point, IsVirtual: true));
                    bridged.Add(i);
                    bridged.Add(bestIdx);
                }
            }

            int remaining = unconnected.Count - bridged.Count;
            info = $"Proximity bridges: {proximityBridges.Count} " +
                $"({remaining} unbridged of {unconnected.Count} unconnected wall ends)";
        }
    }
}
