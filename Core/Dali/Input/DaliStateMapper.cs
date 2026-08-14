#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Input
{
    /// <summary>
    /// The persisted <see cref="DaliModuleState"/> → engine-input mapping, in one place — the DALI analog of
    /// <see cref="TurboSuite.Dmx.Input.DmxStateMapper"/>.
    ///
    /// As with DMX, a single mapping is shared so the headless solve (the DALI demand feeding the TurboZones
    /// Panel Breakdown) and TurboDALI's own loop editing cannot disagree about what the saved job declares.
    /// </summary>
    public static class DaliStateMapper
    {
        /// <summary>
        /// The saved loops as engine declarations, reconciled against the Control Zones that actually exist
        /// now. Mirrors <see cref="TurboSuite.Dmx.Input.DmxStateMapper.ToLoopDeclarations"/> rule-for-rule
        /// (minus the DMX-only reserved-channels field): a zone since renamed or deleted drops out rather
        /// than failing the solve, a zone claimed by two loops sticks to the first (single membership), and
        /// a loop left with no live zones is skipped — an empty declaration would claim a module for nothing.
        /// </summary>
        public static List<DaliLoopDeclaration> ToLoopDeclarations(
            IEnumerable<DaliLoopDto>? loops, IEnumerable<string>? existingZoneNames)
            => Reconcile(loops, existingZoneNames)
                .Select(r => new DaliLoopDeclaration(r.Dto.Name, r.Zones))
                .ToList();

        /// <summary>
        /// The one reconciliation both the job-wide demand (<see cref="ToLoopDeclarations"/> → DaliSolver)
        /// and the panel placement (<see cref="DaliPlacementMapper"/>) run through, so the ordered count and
        /// the placed count derive from the same loop set and cannot disagree. Applies the
        /// shared rules — order by declared Order, drop a zone no longer in the model, single membership
        /// (first loop wins a contested zone), skip a loop left with no live zones — and returns each
        /// surviving loop paired with the DTO it came from, so a caller that needs more than the name +
        /// zones (placement needs the loop's <see cref="DaliLoopDto.AssignedZone"/>) can reach it.
        /// </summary>
        internal static List<ReconciledLoop> Reconcile(
            IEnumerable<DaliLoopDto>? loops, IEnumerable<string>? existingZoneNames)
        {
            var result = new List<ReconciledLoop>();
            if (loops == null) return result;

            var zoneSet = new HashSet<string>(existingZoneNames ?? Enumerable.Empty<string>(),
                                              StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dto in loops.OrderBy(l => l.Order))
            {
                var zones = new List<string>();
                foreach (string zone in dto.ZoneValues ?? new List<string>())
                {
                    if (!zoneSet.Contains(zone) || !used.Add(zone)) continue;
                    zones.Add(zone);
                }
                if (zones.Count == 0) continue;

                result.Add(new ReconciledLoop(dto, zones));
            }
            return result;
        }

        /// <summary>A loop that survived reconciliation: its live (still-in-model, singly-owned) zones,
        /// paired with the persisted DTO for callers that need its other fields.</summary>
        internal readonly struct ReconciledLoop
        {
            public ReconciledLoop(DaliLoopDto dto, IReadOnlyList<string> zones)
            {
                Dto = dto;
                Zones = zones;
            }

            public DaliLoopDto Dto { get; }
            public IReadOnlyList<string> Zones { get; }
        }
    }
}
