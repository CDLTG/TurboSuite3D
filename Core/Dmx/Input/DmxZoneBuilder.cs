#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Input;

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
    /// <see cref="ZoneDesign"/>s. Phase 1 uses the FLAT default — one cluster per zone (correct for
    /// single-location zones, the common case); the in-window cluster sub-builder that splits a
    /// location-spanning zone into multiple clusters is a later refinement (TurboDMX-BuildPlan Phase 1
    /// "cluster derivation"). Fixtures with no zone are counted, not solved. Pure / Revit-free.
    /// </summary>
    public static class DmxZoneBuilder
    {
        public static DmxZoneBuildResult Build(IReadOnlyList<DmxFixtureReading> fixtures)
        {
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));

            int unassigned = 0;
            var order = new List<string>();
            var byZone = new Dictionary<string, List<TapeRun>>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in fixtures)
            {
                string zone = (f.ControlZone ?? "").Trim();
                if (zone.Length == 0) { unassigned++; continue; }

                if (!byZone.TryGetValue(zone, out var runs))
                {
                    runs = new List<TapeRun>();
                    byZone[zone] = runs;
                    order.Add(zone);
                }
                runs.Add(new TapeRun(f.LengthFt, f.WattsPerFt, f.Channels));
            }

            var zones = order.Select(name => new ZoneDesign(name, byZone[name])).ToList();
            return new DmxZoneBuildResult(zones, order, unassigned);
        }
    }
}
