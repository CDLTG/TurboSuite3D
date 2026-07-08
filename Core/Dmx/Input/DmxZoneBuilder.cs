#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx
{
    /// <summary>The result of turning model readings into engine zones: the solvable
    /// <see cref="ZoneDesign"/> list plus the bookkeeping the window surfaces (discovered zone names for
    /// the loop builder, and the count of fixtures with no <c>Control Zone</c> assigned).</summary>
    public sealed class DmxZoneBuildResult
    {
        public DmxZoneBuildResult(IReadOnlyList<ZoneDesign> zones, IReadOnlyList<string> zoneNames, int unassignedFixtures)
        {
            Zones = zones;
            ZoneNames = zoneNames;
            UnassignedFixtures = unassignedFixtures;
        }

        public IReadOnlyList<ZoneDesign> Zones { get; }

        /// <summary>Distinct Control Zone values present, in first-seen order — the loop builder's candidates.</summary>
        public IReadOnlyList<string> ZoneNames { get; }

        /// <summary>Fixtures with an empty <c>Control Zone</c> — excluded from the solve, reported not failed.</summary>
        public int UnassignedFixtures { get; }
    }

    /// <summary>
    /// Groups <see cref="DmxFixtureReading"/>s by their native <c>Control Zone</c> value into engine
    /// <see cref="ZoneDesign"/>s. Default is FLAT — one cluster per zone (correct for single-location zones,
    /// the common case). When the designer has split a location-spanning zone with the in-window cluster
    /// sub-builder, the per-zone <see cref="DmxClusterDto"/> assignments (keyed by fixture ElementId)
    /// partition that zone's runs into named clusters, with any unassigned runs gathered into a visible
    /// "(unclustered)" residual. Packing per cluster is what produces the realizable decoder count ('s
    /// 9-vs-8 effect). Fixtures with no zone are counted, not solved. Pure / Revit-free.
    /// </summary>
    public static class DmxZoneBuilder
    {
        /// <summary>The residual cluster name for a zone's runs not assigned to any declared cluster.</summary>
        public const string ResidualClusterName = "(unclustered)";

        public static DmxZoneBuildResult Build(IReadOnlyList<DmxFixtureReading> fixtures,
                                               IReadOnlyList<DmxClusterDto>? clusters = null)
        {
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));

            int unassigned = 0;
            var order = new List<string>();
            // Keep each fixture as a bundler Item (carries its ElementId for cluster binding) so the
            // cluster/flat/residual paths can coalesce chains of connectable fixtures into bundle runs.
            var byZone = new Dictionary<string, List<DmxBundler.Item>>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in fixtures)
            {
                string zone = (f.ControlZone ?? "").Trim();
                if (zone.Length == 0) { unassigned++; continue; }

                if (!byZone.TryGetValue(zone, out var runs))
                {
                    runs = new List<DmxBundler.Item>();
                    byZone[zone] = runs;
                    order.Add(zone);
                }
                runs.Add(new DmxBundler.Item(
                    f.ElementId, new TapeRun(f.LengthFt, f.WattsPerFt, f.Channels), f.MaxPerBundle, f.TypeMark));
            }

            var clustersByZone = (clusters ?? new List<DmxClusterDto>())
                .GroupBy(c => (c.ZoneValue ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var zones = order
                .Select(name => BuildZone(name, byZone[name],
                                          clustersByZone.TryGetValue(name, out var zc) ? zc : null))
                .ToList();
            return new DmxZoneBuildResult(zones, order, unassigned);
        }

        private static ZoneDesign BuildZone(string zone, List<DmxBundler.Item> runs,
                                            List<DmxClusterDto>? zoneClusters)
        {
            // No declared clusters ⇒ flat (one cluster per zone). Bundle within the flat cluster.
            if (zoneClusters == null || zoneClusters.Count == 0)
                return new ZoneDesign(zone, DmxBundler.Bundle(runs));

            // Bind each run to its cluster (last declaration wins, so a reassign just re-lists the run).
            var runToCluster = new Dictionary<long, int>();
            for (int ci = 0; ci < zoneClusters.Count; ci++)
                foreach (var id in zoneClusters[ci].RunElementIds ?? new List<long>())
                    runToCluster[id] = ci;

            // Bundling happens PER cluster (a chain can't span walls) — coalesce each cluster's fixtures
            // independently after the partition.
            var clusterItems = new List<DmxBundler.Item>[zoneClusters.Count];
            for (int i = 0; i < clusterItems.Length; i++) clusterItems[i] = new List<DmxBundler.Item>();
            var residual = new List<DmxBundler.Item>();

            foreach (var item in runs)
            {
                if (runToCluster.TryGetValue(item.Id, out var ci)) clusterItems[ci].Add(item);
                else residual.Add(item);
            }

            var built = new List<RunCluster>();
            for (int ci = 0; ci < zoneClusters.Count; ci++)
                if (clusterItems[ci].Count > 0)
                    built.Add(new RunCluster(zoneClusters[ci].Name, DmxBundler.Bundle(clusterItems[ci])));
            if (residual.Count > 0)
                built.Add(new RunCluster(ResidualClusterName, DmxBundler.Bundle(residual)));

            // A zone with fixtures always yields ≥1 cluster; guard the degenerate all-empty case.
            return built.Count > 0 ? new ZoneDesign(zone, built)
                                   : new ZoneDesign(zone, DmxBundler.Bundle(runs));
        }
    }
}
