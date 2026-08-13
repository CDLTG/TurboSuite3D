#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>One node the walk orders: a stable <see cref="Key"/> (the circuit's UniqueId) and its plan
    /// <see cref="Point"/> (null ⇒ no computable centroid — deterministic key-order fallback).</summary>
    public readonly struct WalkNode
    {
        public WalkNode(string key, DaliPoint? point)
        {
            Key = key ?? "";
            Point = point;
        }

        public string Key { get; }
        public DaliPoint? Point { get; }
    }

    /// <summary>
    /// The spatial ordering step for DALI addressing (plan §Ordering, H3) — a <b>pure port of TurboWire's
    /// <c>FixtureOrderingService</c></b> (double-farthest-point diameter → greedy nearest-neighbor → shorter
    /// of the two paths), lifted off Revit types so it walks plain points and is unit-testable with synthetic
    /// layouts. Ported by COPY, not shared reference — <c>Shim/Wire</c> keeps its own copy; the algorithm is
    /// shared the way <c>DmxLockReconciler</c> is a pattern, not shared code.
    ///
    /// <para>Two adaptations over the TurboWire original:</para>
    /// <list type="bullet">
    ///   <item><b>NW seed.</b> TurboWire returns the shorter path with an arbitrary end first; DALI orients it
    ///   so the <b>NW-most endpoint</b> (max Y, then min X) is first, so <c>-01</c> reads top-to-bottom,
    ///   left-to-right the way a plan is read. A predictable origin the bare "shorter of two" rule lacks.</item>
    ///   <item><b>Deterministic ties.</b> Nearest-neighbor and endpoint selection break exact-distance ties by
    ///   ordinal key, and nodes with no point sort by key and append after, so a given model state always
    ///   yields the same order (the lock reconcile depends on it).</item>
    /// </list>
    /// </summary>
    public static class ProximityWalk
    {
        private const double Eps = 1e-9;

        /// <summary>Order the nodes into an NW-seeded proximity chain, appending point-less nodes by key.</summary>
        public static List<string> NwSeededOrder(IReadOnlyList<WalkNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return new List<string>();

            var located = nodes.Where(n => n.Point.HasValue).ToList();
            var unlocated = nodes.Where(n => !n.Point.HasValue)
                                 .Select(n => n.Key)
                                 .OrderBy(k => k, StringComparer.Ordinal)
                                 .ToList();

            List<WalkNode> chain =
                located.Count <= 1 ? located :
                located.Count == 2 ? Orient(located) :
                                     Chain(located);

            var result = chain.Select(n => n.Key).ToList();
            result.AddRange(unlocated);
            return result;
        }

        // Double-farthest-point endpoints → greedy nearest-neighbor from each → shorter path → NW-oriented.
        private static List<WalkNode> Chain(List<WalkNode> nodes)
        {
            WalkNode a = Farthest(nodes[0], nodes);
            WalkNode b = Farthest(a, nodes);

            var pathA = NearestNeighborFrom(a, nodes);
            var pathB = NearestNeighborFrom(b, nodes);

            var shorter = Length(pathA) <= Length(pathB) + Eps ? pathA : pathB;
            return Orient(shorter);
        }

        // The node farthest from `from`; exact-distance ties break by ordinal key (determinism).
        private static WalkNode Farthest(WalkNode from, List<WalkNode> nodes)
        {
            WalkNode best = from;
            double bestDist = -1;
            foreach (var n in nodes)
            {
                double d = P(from).DistanceTo(P(n));
                if (d > bestDist + Eps ||
                    (Math.Abs(d - bestDist) <= Eps && string.CompareOrdinal(n.Key, best.Key) < 0))
                {
                    bestDist = d;
                    best = n;
                }
            }
            return best;
        }

        private static List<WalkNode> NearestNeighborFrom(WalkNode start, List<WalkNode> nodes)
        {
            var ordered = new List<WalkNode> { start };
            var remaining = new HashSet<string>(nodes.Select(n => n.Key));
            remaining.Remove(start.Key);

            WalkNode current = start;
            while (remaining.Count > 0)
            {
                WalkNode? closest = null;
                double closestDist = double.MaxValue;
                foreach (var n in nodes)
                {
                    if (!remaining.Contains(n.Key)) continue;
                    double d = P(current).DistanceTo(P(n));
                    if (d < closestDist - Eps ||
                        (Math.Abs(d - closestDist) <= Eps &&
                         (closest == null || string.CompareOrdinal(n.Key, closest.Value.Key) < 0)))
                    {
                        closestDist = d;
                        closest = n;
                    }
                }
                if (closest == null) break;
                ordered.Add(closest.Value);
                remaining.Remove(closest.Value.Key);
                current = closest.Value;
            }
            return ordered;
        }

        // Flip the chain so the NW-most END leads (max Y, then min X, then ordinal key).
        private static List<WalkNode> Orient(List<WalkNode> chain)
        {
            if (chain.Count < 2) return chain;
            if (IsMoreNw(chain[chain.Count - 1], chain[0]))
            {
                var reversed = new List<WalkNode>(chain);
                reversed.Reverse();
                return reversed;
            }
            return chain;
        }

        // Is `a` more north-west than `b`? Higher Y wins; equal Y ⇒ smaller X; exact tie ⇒ smaller key.
        private static bool IsMoreNw(WalkNode a, WalkNode b)
        {
            DaliPoint pa = P(a), pb = P(b);
            if (Math.Abs(pa.Y - pb.Y) > Eps) return pa.Y > pb.Y;
            if (Math.Abs(pa.X - pb.X) > Eps) return pa.X < pb.X;
            return string.CompareOrdinal(a.Key, b.Key) < 0;
        }

        private static double Length(List<WalkNode> path)
        {
            double total = 0;
            for (int i = 0; i < path.Count - 1; i++)
                total += P(path[i]).DistanceTo(P(path[i + 1]));
            return total;
        }

        private static DaliPoint P(WalkNode n) => n.Point!.Value;
    }
}
