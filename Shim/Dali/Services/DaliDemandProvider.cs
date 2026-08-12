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

        /// <summary>DALI loads per Control Zone value, where <b>a load is a DALI address = one circuit</b>,
        /// not one fixture. Shared-driver tape (several runs on one unassigned circuit) collapses to one
        /// load; a downlight on its own circuit stays one — the "one driver = one circuit = one address"
        /// convention. The collapse arithmetic is the pure <see cref="DaliLoadCounter"/>; here we only read
        /// the model into <see cref="DaliFixtureReading"/>s.
        ///
        /// <paramref name="totalDaliFixtures"/> returns every DALI fixture seen (circuited or not, zoned or
        /// not), so the caller can still tell "hardware present but undeclared" from "no DALI at all".
        ///
        /// The driver/decoder that shares a tape circuit is a lighting <i>device</i>, not a fixture, so it is
        /// never collected — it neither adds nor removes a load.</summary>
        internal static Dictionary<string, int> CountDaliLoadsByZone(Document doc, out int totalDaliFixtures)
        {
            var lightingCatId = new ElementId(BuiltInCategory.OST_LightingFixtures);
            var readings = new List<DaliFixtureReading>();
            var circuited = new HashSet<ElementId>();

            // Pass 1 — circuit-first: each DALI circuit's fixtures carry that circuit's id, so they collapse
            // to a single address in DaliLoadCounter. Reading circuit.Elements (not a fixture→system lookup)
            // matches how ZonesCollectorService walks circuits and keeps the driver device out by category.
            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>();

            foreach (var circuit in circuits)
            {
                string circuitKey = circuit.UniqueId;
                foreach (Element el in circuit.Elements)
                {
                    if (el is FamilyInstance fi && fi.Category?.Id == lightingCatId && IsDali(fi))
                    {
                        circuited.Add(fi.Id);
                        readings.Add(new DaliFixtureReading(circuitKey, ReadZone(fi)));
                    }
                }
            }

            // Pass 2 — uncircuited DALI fixtures: their own load (empty circuit key), until the designer wires
            // them onto a circuit, at which point pass 1 collapses them.
            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in fixtures)
            {
                if (circuited.Contains(fi.Id) || !IsDali(fi)) continue;
                readings.Add(new DaliFixtureReading("", ReadZone(fi)));
            }

            totalDaliFixtures = readings.Count;
            return DaliLoadCounter.CountByZone(readings);
        }

        private static bool IsDali(FamilyInstance fi)
            => ParameterHelper.GetDimmingProtocol(fi).Trim()
                .Equals(DaliProtocol, StringComparison.OrdinalIgnoreCase);

        private static string ReadZone(FamilyInstance fi)
            => fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim() ?? "";
    }
}
