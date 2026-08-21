#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dali.Input
{
    /// <summary>
    /// Reduces <see cref="DaliUnitReading"/>s to <b>loads per Control Zone</b>, where the unit of a load is a
    /// single <b>addressable unit = one DALI address</b> — a driver device or a self-driven downlight fixture.
    /// This is the per-unit fix for the 64/bus warning: a circuit carrying N drivers presents <b>N</b>
    /// addresses (not one), so a zone's load count is its true DALI address count — exactly what the 64-cap
    /// limits. The old "one load per circuit" collapse lived here; it is gone, because the shim's
    /// <c>DaliUnitEnumerator</c> now hands us units directly (each already one load).
    ///
    /// <b>Rules:</b>
    /// <list type="bullet">
    ///   <item>Each unit with a non-blank zone contributes <b>one</b> load to that zone.</item>
    ///   <item>A unit whose zone is blank adds no load (unassigned hardware — the demand provider still sees
    ///   the units for its "hardware present but undeclared" check).</item>
    /// </list>
    ///
    /// The per-circuit zone reconciliation (all a circuit's fixtures should agree on the zone; a blank one is
    /// tolerated as long as another carries it) now happens upstream in the enumerator, which stamps every
    /// driver unit with its circuit's resolved zone. Counting is therefore a flat tally.
    /// </summary>
    public static class DaliLoadCounter
    {
        public static Dictionary<string, int> CountByZone(IEnumerable<DaliUnitReading>? units)
        {
            var byZone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in units ?? Enumerable.Empty<DaliUnitReading>())
                if (u.Zone.Length > 0)
                    byZone[u.Zone] = byZone.TryGetValue(u.Zone, out int n) ? n + 1 : 1;
            return byZone;
        }
    }
}
