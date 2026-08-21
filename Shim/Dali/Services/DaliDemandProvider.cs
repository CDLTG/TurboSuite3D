#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Dali;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// DALI as a control-subsystem demand provider — the third after DMX and shades. Reads the persisted
    /// loops (<see cref="DaliStorageService"/>) and the DALI fixtures in the model, counts the loads on
    /// each loop, and hands the per-loop tallies to the pure <see cref="DaliSolver"/>, which recommends the
    /// LQSE2-1DALUNV-D module count (= loop count) and the QS-link device/leg budget.
    ///
    /// <b>This reports the job-wide order + link budget, not placement.</b> Which ZONE N panel each module
    /// sits in is the designer's required per-loop assignment, declared in TurboDALI and fed to the allocator
    /// as a separate <c>daliModulesByZone</c> map. Here we only answer "how many
    /// modules does the job need, and what do they cost the link" — the single BOM/link authority, so the
    /// placed panel slots are deliberately excluded from both (see <c>ModuleResult.OrderedBySubsystem</c>).
    ///
    /// <b>Identity — the fixture's Dimming Protocol = DALI</b>, the same membership rule TurboDMX uses for
    /// its own protocol. Each DALI fixture is one addressable load = one switch leg; a loop's loads are the
    /// fixtures whose Control Zone falls in that loop.
    ///
    /// <b>Must not throw</b> (see the interface): every read is wrapped, and a failure becomes an Unsolvable
    /// demand so a half-declared DALI job never breaks the BOM.
    /// </summary>
    public sealed class DaliDemandProvider : IControlSubsystemDemandProvider
    {
        private readonly Document _doc;

        public DaliDemandProvider(Document doc) => _doc = doc;

        public ControlSubsystemDemand GetDemand()
        {
            if (_doc == null) return ControlSubsystemDemand.None(DaliSolver.SubsystemName);
            try
            {
                var state = DaliStorageService.Load(_doc);
                var loadsByZone = CountDaliLoadsByZone(_doc, out int totalDaliUnits);

                // Reconcile the declared loops against the zones that actually carry DALI fixtures (drops a
                // renamed/deleted zone, single membership, skips an empty loop) — the same rules the tab's
                // placement will use, so the ordered count and the placed count cannot disagree.
                var declarations = DaliStateMapper.ToLoopDeclarations(state.Loops, loadsByZone.Keys);

                var tallies = declarations
                    .Select(d => new DaliLoopTally(
                        d.Name,
                        d.ZoneNames.Sum(z => loadsByZone.TryGetValue(z, out int n) ? n : 0)))
                    .ToList();

                if (tallies.Count == 0)
                {
                    // DALI hardware is in the model but nothing is declared to control it — the modules
                    // would never be ordered and nothing would say why. Speak up (the unzoned-DMX principle:
                    // real uncounted hardware is never silent). A clean nothing only when there is no DALI.
                    if (totalDaliUnits > 0)
                        return ControlSubsystemDemand.Unsolvable(
                            DaliSolver.SubsystemName,
                            $"{totalDaliUnits} DALI load"
                            + (totalDaliUnits == 1 ? "" : "s")
                            + " present but no loops declared — declare loops in TurboDALI.");
                    return ControlSubsystemDemand.None(DaliSolver.SubsystemName);
                }

                return DaliSolver.Solve(tallies);
            }
            catch (Exception ex)
            {
                return ControlSubsystemDemand.Unsolvable(
                    DaliSolver.SubsystemName, "could not read DALI loops — " + ex.Message);
            }
        }

        /// <summary>DALI loads per Control Zone value, where <b>a load is one addressable unit = one DALI
        /// address</b> — a driver device or a self-driven downlight fixture, NOT one tape fixture and NOT one
        /// circuit. A circuit carrying N drivers presents N addresses; a self-driven downlight is one. The
        /// unit enumeration is the shared <see cref="DaliUnitEnumerator"/> (the same walk the addressing read
        /// consumes, so the counted total and the issued-address count cannot disagree); the flat tally is the
        /// pure <see cref="DaliLoadCounter"/>.
        ///
        /// <paramref name="totalDaliUnits"/> returns every addressable unit seen (circuited or not, zoned or
        /// not), so the caller can still tell "hardware present but undeclared" from "no DALI at all".</summary>
        internal static Dictionary<string, int> CountDaliLoadsByZone(Document doc, out int totalDaliUnits)
        {
            var units = DaliUnitEnumerator.Enumerate(doc);
            totalDaliUnits = units.Count;
            return DaliLoadCounter.CountByZone(units);
        }
    }
}
