#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dali.Input
{
    /// <summary>One DALI lighting fixture as read from the model: the circuit it sits on (its DALI address)
    /// and its Control Zone. <see cref="CircuitKey"/> empty ⇒ the fixture is not on a circuit yet.</summary>
    public readonly struct DaliFixtureReading
    {
        public DaliFixtureReading(string? circuitKey, string? zone)
        {
            CircuitKey = (circuitKey ?? "").Trim();
            Zone = (zone ?? "").Trim();
        }

        /// <summary>Stable id of the fixture's circuit (the DALI address). Empty = uncircuited.</summary>
        public string CircuitKey { get; }

        /// <summary>The fixture's Control Zone value. Empty = unzoned (joins no loop).</summary>
        public string Zone { get; }
    }

    /// <summary>
    /// Reduces DALI fixtures to <b>loads per Control Zone</b>, where the unit of a load is a <b>DALI address =
    /// one circuit</b>, not one fixture. This is the fix for shared-driver tape: six tape runs wired to one
    /// remote DALI driver sit on one circuit and present <b>one</b> address, so they must collapse to one
    /// load — while a downlight on its own circuit stays one load. The designer's convention ("one driver =
    /// one circuit = one address", confirmed 2026-08-12) makes counting by circuit exactly right for both.
    ///
    /// <b>Rules:</b>
    /// <list type="bullet">
    ///   <item>Each distinct circuit contributes <b>one</b> load to its zone — the collapse.</item>
    ///   <item>A circuit's zone is the first non-blank Control Zone among its fixtures (they should all
    ///   agree; a blank one is tolerated as long as another fixture on the circuit carries the zone).</item>
    ///   <item>A circuit whose fixtures are all unzoned adds no load (an unassigned address — the demand
    ///   provider still sees the fixtures for its "hardware present but undeclared" check).</item>
    ///   <item>An <b>uncircuited</b> DALI fixture counts as its own load — conservative (never under-orders),
    ///   and it collapses into the circuit's single load the moment the designer wires it.</item>
    /// </list>
    ///
    /// The driver/decoder device itself never appears here: it is a lighting <i>device</i>, and the shim only
    /// feeds lighting <i>fixtures</i>, so a driver sharing the circuit neither adds nor removes a load.
    /// </summary>
    public static class DaliLoadCounter
    {
        public static Dictionary<string, int> CountByZone(IEnumerable<DaliFixtureReading>? fixtures)
        {
            var byZone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var circuitZone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var circuitOrder = new List<string>();

            foreach (var f in fixtures ?? Enumerable.Empty<DaliFixtureReading>())
            {
                if (f.CircuitKey.Length == 0)
                {
                    if (f.Zone.Length > 0) Add(byZone, f.Zone);   // uncircuited ⇒ its own load
                    continue;
                }

                if (!circuitZone.TryGetValue(f.CircuitKey, out string? zone))
                {
                    circuitZone[f.CircuitKey] = f.Zone;           // first fixture on this circuit (may be blank)
                    circuitOrder.Add(f.CircuitKey);
                }
                else if (zone.Length == 0 && f.Zone.Length > 0)
                {
                    circuitZone[f.CircuitKey] = f.Zone;           // upgrade a blank to the circuit's real zone
                }
            }

            // One load per circuit that resolved to a zone.
            foreach (string key in circuitOrder)
            {
                string zone = circuitZone[key];
                if (zone.Length > 0) Add(byZone, zone);
            }

            return byZone;
        }

        private static void Add(Dictionary<string, int> byZone, string zone)
            => byZone[zone] = byZone.TryGetValue(zone, out int n) ? n + 1 : 1;
    }
}
