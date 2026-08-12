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
    /// As with DMX, a single mapping is shared so a future headless solve (the DALI demand feeding the
    /// TurboZones Panel Breakdown) and the TurboZones DALI tab cannot disagree about what the saved job
    /// declares. Phase 3b provides only the loop mapping; the solve itself is Phase 3c.
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
        {
            var declarations = new List<DaliLoopDeclaration>();
            if (loops == null) return declarations;

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

                declarations.Add(new DaliLoopDeclaration(dto.Name, zones));
            }
            return declarations;
        }
    }
}
