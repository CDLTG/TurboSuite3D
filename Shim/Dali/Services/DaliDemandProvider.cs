#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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
    /// sits in is the designer's required per-loop assignment, collected by the TurboZones DALI tab and fed
    /// to the allocator as a separate <c>daliModulesByZone</c> map (Phase 3e). Here we only answer "how many
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
        private const string DaliProtocol = "DALI";

        private readonly Document _doc;

        public DaliDemandProvider(Document doc) => _doc = doc;

        public ControlSubsystemDemand GetDemand()
        {
            if (_doc == null) return ControlSubsystemDemand.None(DaliSolver.SubsystemName);
            try
            {
                var state = DaliStorageService.Load(_doc);
                var loadsByZone = CountDaliLoadsByZone(_doc, out int totalDaliFixtures);

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
                    if (totalDaliFixtures > 0)
                        return ControlSubsystemDemand.Unsolvable(
                            DaliSolver.SubsystemName,
                            $"{totalDaliFixtures} DALI fixture"
                            + (totalDaliFixtures == 1 ? " is" : "s are")
                            + " in the model but no DALI loops are declared — declare loops in TurboZones "
                            + "so their modules can be counted.");
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

        /// <summary>DALI fixtures (Dimming Protocol = DALI) counted per Control Zone value — one load each.
        /// <paramref name="totalDaliFixtures"/> returns every DALI fixture seen, including those with no
        /// Control Zone (which join no loop), so the caller can tell "hardware present but undeclared" from
        /// "no DALI at all".</summary>
        internal static Dictionary<string, int> CountDaliLoadsByZone(Document doc, out int totalDaliFixtures)
        {
            var byZone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            totalDaliFixtures = 0;

            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in fixtures)
            {
                if (!ParameterHelper.GetDimmingProtocol(fi).Trim()
                        .Equals(DaliProtocol, StringComparison.OrdinalIgnoreCase))
                    continue;

                totalDaliFixtures++;

                string zone = fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim() ?? "";
                if (zone.Length == 0) continue;   // a DALI fixture with no Control Zone can join no loop

                byZone[zone] = byZone.TryGetValue(zone, out int n) ? n + 1 : 1;
            }

            return byZone;
        }
    }
}
