#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Computes an optimized assignment of circuits to panels within each location.
    /// Priority: (1) minimize modules, (2) minimize panels used, (3) spread types evenly.
    /// Pure computation — no Revit writes.
    /// </summary>
    public static class CircuitRedistributionService
    {
        public static RedistributionPlan ComputePlan(
            List<ZonesCircuitData> circuits,
            BrandConfig brand,
            PanelAllocationResult currentResult)
        {
            var plan = new RedistributionPlan();
            var proposedAssignment = new Dictionary<ElementId, string>();

            foreach (var location in currentResult.Locations)
            {
                var panelNames = location.Panels.Select(p => p.PanelName).ToList();
                int numPanels = panelNames.Count;

                if (numPanels <= 1)
                {
                    AssignCurrentPanels(circuits, panelNames, proposedAssignment);
                    plan.LocationSummaries[location.LocationNumber] = BuildLocationSummary(
                        location.LocationNumber, circuits, brand, location.Panels, proposedAssignment);
                    continue;
                }

                // Skip optimization for overloaded locations
                if (location.IsOverCapacity)
                {
                    AssignCurrentPanels(circuits, panelNames, proposedAssignment);
                    plan.LocationSummaries[location.LocationNumber] = BuildLocationSummary(
                        location.LocationNumber, circuits, brand, location.Panels, proposedAssignment);
                    continue;
                }

                // Collect all circuits in this location
                var panelNameSet = new HashSet<string>(panelNames, StringComparer.OrdinalIgnoreCase);
                var locationCircuits = circuits
                    .Where(c => !string.IsNullOrWhiteSpace(c.PanelName)
                                && panelNameSet.Contains(c.PanelName))
                    .ToList();

                // Panel capacity lookup
                var panelCapacity = location.Panels
                    .ToDictionary(p => p.PanelName, p => p.PanelCapacity, StringComparer.OrdinalIgnoreCase);

                // Group circuits by dimming type, compute consolidated module count
                var typeGroups = locationCircuits
                    .GroupBy(c => c.DimmingType, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new TypeGroup
                    {
                        DimmingType = g.Key,
                        Circuits = g.OrderBy(c => c.CircuitNumber).ToList(),
                        ModuleCapacity = brand.GetModuleCapacity(g.Key),
                        MinModules = PanelAllocationService.CalculateModuleCount(
                            g.Count(), brand.GetModuleCapacity(g.Key))
                    })
                    .OrderByDescending(tg => tg.MinModules)
                    .ToList();

                int totalModulesNeeded = typeGroups.Sum(tg => tg.MinModules);

                // Phase 1: Determine minimum panels needed (first-fit decreasing)
                var sortedPanels = panelNames
                    .OrderByDescending(p => panelCapacity[p])
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int accumulatedCapacity = 0;
                var usedPanels = new List<string>();
                foreach (var pn in sortedPanels)
                {
                    usedPanels.Add(pn);
                    accumulatedCapacity += panelCapacity[pn];
                    if (accumulatedCapacity >= totalModulesNeeded)
                        break;
                }

                // Phase 2: Spread types evenly across used panels
                // Track remaining capacity per used panel
                var remainingCapacity = usedPanels
                    .ToDictionary(p => p, p => panelCapacity[p], StringComparer.OrdinalIgnoreCase);

                // Distribution: panel → type → module count
                var distribution = usedPanels
                    .ToDictionary(p => p,
                        p => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var tg in typeGroups)
                {
                    SpreadTypeAcrossPanels(tg.DimmingType, tg.MinModules, usedPanels, remainingCapacity, distribution);
                }

                // Phase 3: Assign circuits to panels based on distribution
                AssignCircuitsFromDistribution(typeGroups, distribution, brand, proposedAssignment);

                // Unused panels get no circuits — they'll remain empty
                plan.LocationSummaries[location.LocationNumber] = BuildLocationSummary(
                    location.LocationNumber, circuits, brand, location.Panels, proposedAssignment);
            }

            // Build move list — only circuits that change panels
            foreach (var circuit in circuits)
            {
                if (!proposedAssignment.TryGetValue(circuit.CircuitId, out string toPanel))
                    continue;

                if (!string.Equals(circuit.PanelName, toPanel, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Moves.Add(new CircuitMove
                    {
                        CircuitId = circuit.CircuitId,
                        CircuitNumber = circuit.CircuitNumber,
                        DimmingType = circuit.DimmingType,
                        FromPanel = circuit.PanelName,
                        ToPanel = toPanel
                    });
                }
            }

            return plan;
        }

        /// <summary>
        /// Spreads a type's modules as evenly as possible across the used panels,
        /// respecting remaining capacity per panel.
        /// </summary>
        private static void SpreadTypeAcrossPanels(
            string dimmingType,
            int modulesNeeded,
            List<string> usedPanels,
            Dictionary<string, int> remainingCapacity,
            Dictionary<string, Dictionary<string, int>> distribution)
        {
            int modulesLeft = modulesNeeded;

            // Count panels with remaining capacity
            var panelsWithRoom = usedPanels.Where(p => remainingCapacity[p] > 0).ToList();
            if (panelsWithRoom.Count == 0)
                return;

            // Compute even share
            int share = (int)Math.Ceiling((double)modulesLeft / panelsWithRoom.Count);

            foreach (var panel in panelsWithRoom)
            {
                if (modulesLeft <= 0) break;

                int toPlace = Math.Min(share, Math.Min(modulesLeft, remainingCapacity[panel]));
                if (toPlace > 0)
                {
                    distribution[panel][dimmingType] = toPlace;
                    remainingCapacity[panel] -= toPlace;
                    modulesLeft -= toPlace;
                }
            }

            // If any modules remain (uneven capacities), place on panels with remaining room
            if (modulesLeft > 0)
            {
                foreach (var panel in usedPanels.OrderByDescending(p => remainingCapacity[p]))
                {
                    if (modulesLeft <= 0) break;
                    if (remainingCapacity[panel] <= 0) continue;

                    int toPlace = Math.Min(modulesLeft, remainingCapacity[panel]);
                    if (!distribution[panel].ContainsKey(dimmingType))
                        distribution[panel][dimmingType] = 0;
                    distribution[panel][dimmingType] += toPlace;
                    remainingCapacity[panel] -= toPlace;
                    modulesLeft -= toPlace;
                }
            }
        }

        /// <summary>
        /// Given the module distribution per panel per type, assigns specific circuits
        /// to panels. For each type, distributes circuits proportionally to the module
        /// count allocated to each panel.
        /// </summary>
        private static void AssignCircuitsFromDistribution(
            List<TypeGroup> typeGroups,
            Dictionary<string, Dictionary<string, int>> distribution,
            BrandConfig brand,
            Dictionary<ElementId, string> proposedAssignment)
        {
            foreach (var tg in typeGroups)
            {
                // Get panels that have modules of this type, sorted by panel name
                var panelsForType = distribution
                    .Where(kvp => kvp.Value.ContainsKey(tg.DimmingType) && kvp.Value[tg.DimmingType] > 0)
                    .Select(kvp => new { Panel = kvp.Key, Modules = kvp.Value[tg.DimmingType] })
                    .OrderBy(x => x.Panel, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int circuitIdx = 0;
                int totalCircuits = tg.Circuits.Count;

                for (int p = 0; p < panelsForType.Count; p++)
                {
                    int modulesOnPanel = panelsForType[p].Modules;
                    int remainingPanels = panelsForType.Count - p;
                    int remainingCircuits = totalCircuits - circuitIdx;

                    // How many circuits should go on this panel?
                    // Use module count to determine proportional share
                    int circuitsForPanel;
                    if (p == panelsForType.Count - 1)
                    {
                        // Last panel gets all remaining
                        circuitsForPanel = remainingCircuits;
                    }
                    else
                    {
                        // Find max circuits that fit in the allocated modules
                        circuitsForPanel = FindMaxCircuitsForSlots(
                            remainingCircuits, modulesOnPanel, tg.ModuleCapacity);

                        // If binary search gives 0 but we have modules, place at least some
                        if (circuitsForPanel == 0 && modulesOnPanel > 0)
                            circuitsForPanel = Math.Min(remainingCircuits, modulesOnPanel * tg.ModuleCapacity);
                    }

                    string panelName = panelsForType[p].Panel;
                    for (int c = 0; c < circuitsForPanel && circuitIdx < totalCircuits; c++, circuitIdx++)
                    {
                        proposedAssignment[tg.Circuits[circuitIdx].CircuitId] = panelName;
                    }
                }
            }
        }

        /// <summary>
        /// Finds the maximum number of circuits (up to maxCircuits) whose module count
        /// fits within the given number of available module slots.
        /// </summary>
        internal static int FindMaxCircuitsForSlots(int maxCircuits, int availableSlots, int moduleCapacity)
        {
            if (availableSlots <= 0)
                return 0;

            // Binary search for the maximum N where CalculateModuleCount(N, cap) <= availableSlots
            int lo = 0, hi = maxCircuits;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (PanelAllocationService.CalculateModuleCount(mid, moduleCapacity) <= availableSlots)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        private static void AssignCurrentPanels(
            List<ZonesCircuitData> circuits,
            List<string> panelNames,
            Dictionary<ElementId, string> proposedAssignment)
        {
            var panelNameSet = new HashSet<string>(panelNames, StringComparer.OrdinalIgnoreCase);
            foreach (var circuit in circuits)
            {
                if (!string.IsNullOrWhiteSpace(circuit.PanelName) && panelNameSet.Contains(circuit.PanelName))
                    proposedAssignment[circuit.CircuitId] = circuit.PanelName;
            }
        }

        private static LocationSummaryPair BuildLocationSummary(
            int locationNumber,
            List<ZonesCircuitData> allCircuits,
            BrandConfig brand,
            List<PanelResult> panelResults,
            Dictionary<ElementId, string> proposedAssignment)
        {
            var panelNames = panelResults.Select(p => p.PanelName).ToList();
            var panelNameSet = new HashSet<string>(panelNames, StringComparer.OrdinalIgnoreCase);
            var locationCircuits = allCircuits
                .Where(c => !string.IsNullOrWhiteSpace(c.PanelName) && panelNameSet.Contains(c.PanelName))
                .ToList();

            var before = new List<PanelSummary>();
            var after = new List<PanelSummary>();

            foreach (var pr in panelResults)
            {
                var beforeCircuits = locationCircuits
                    .Where(c => string.Equals(c.PanelName, pr.PanelName, StringComparison.OrdinalIgnoreCase));
                before.Add(BuildPanelSummary(pr.PanelName, pr.PanelCapacity, beforeCircuits, brand));

                var afterCircuits = locationCircuits
                    .Where(c => proposedAssignment.TryGetValue(c.CircuitId, out var tp)
                                && string.Equals(tp, pr.PanelName, StringComparison.OrdinalIgnoreCase));
                after.Add(BuildPanelSummary(pr.PanelName, pr.PanelCapacity, afterCircuits, brand));
            }

            return new LocationSummaryPair
            {
                LocationNumber = locationNumber,
                Before = before,
                After = after
            };
        }

        private static PanelSummary BuildPanelSummary(
            string panelName, int panelCapacity,
            IEnumerable<ZonesCircuitData> circuits, BrandConfig brand)
        {
            var types = circuits
                .GroupBy(c => c.DimmingType, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TypeSummary
                {
                    DimmingType = g.Key,
                    CircuitCount = g.Count(),
                    ModuleCount = PanelAllocationService.CalculateModuleCount(
                        g.Count(), brand.GetModuleCapacity(g.Key))
                })
                .ToList();

            return new PanelSummary
            {
                PanelName = panelName,
                PanelCapacity = panelCapacity,
                TotalModules = types.Sum(t => t.ModuleCount),
                Types = types
            };
        }

        private class TypeGroup
        {
            public string DimmingType { get; set; }
            public List<ZonesCircuitData> Circuits { get; set; }
            public int ModuleCapacity { get; set; }
            public int MinModules { get; set; }
        }
    }
}
