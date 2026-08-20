#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md.
///
/// Overwrite-safe by design: everything in <see cref="Execute"/> is diagnostics-only scratch. When
/// you need to answer a question the running model can settle (a parameter's StorageType/writability,
/// whether an API member exists on this version, a family's connectors/geometry), clobber whatever
/// stub is here with a probe, have the user build and run it, and read the dialog. No prior spike is
/// worth preserving. It ships gated behind ExperimentalCommandsEnabled, so it's dev-only.
///
/// CURRENT PROBE — DMX per-decoder circuit feasibility (plan: TurboDMX real circuits). Answers the two
/// in-model questions the plan defers to implementation time, in ONE run, then rolls everything back so
/// the model is untouched:
///   A. Teardown — does doc.Delete(circuit.Id) delete the ElectricalSystem and FREE its member fixtures
///      (leaving them un-circuited so reconcile can re-circuit them), or must we delete members?
///   B. Create throws-vs-null — when a member is already on a circuit, does ElectricalSystem.Create THROW
///      or return null? (CircuitService.CreateCircuit only handles the null return.)
///   C. Apparent load — does an unpaneled "&lt;unnamed&gt;" circuit report RBS_ELEC_APPARENT_LOAD as the
///      tape fixtures' load ALONE (decoder + driver connectors at 0.00 VA add nothing)?
///
/// USAGE: select the members of ONE intended DMX circuit — the tape lighting fixtures PLUS the decoder
/// and driver lighting devices — then run TurboSpike. Read the dialog. Nothing is committed.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        var doc = uidoc.Document;

        var selIds = uidoc.Selection.GetElementIds().ToList();
        if (selIds.Count == 0)
        {
            TaskDialog.Show("TurboSpike",
                "Select ONE DMX circuit's members first: the tape lighting fixtures + the decoder + the "
                + "driver (lighting devices). Then run TurboSpike again.");
            return Result.Cancelled;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Selection: {selIds.Count} element(s).");
        sb.AppendLine(DescribeSelection(doc, selIds));
        sb.AppendLine();

        // One transaction for the whole probe; rolled back at the end so Create/Delete never persist.
        using var tx = new Transaction(doc, "TurboSpike — DMX circuit probe (rolled back)");
        tx.Start();
        try
        {
            // ── Create the circuit from the exact selection (no panel) ──────────────────────────
            ElectricalSystem circuit;
            try
            {
                circuit = ElectricalSystem.Create(doc, selIds, ElectricalSystemType.PowerCircuit);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"CREATE #1 THREW: {ex.GetType().Name}: {ex.Message}");
                sb.AppendLine("→ Create rejected the mixed member set. Check connectors/voltage on the selection.");
                tx.RollBack();
                Show(sb);
                return Result.Succeeded;
            }

            if (circuit == null)
            {
                sb.AppendLine("CREATE #1 returned NULL — Create rejected the member set (no exception).");
                tx.RollBack();
                Show(sb);
                return Result.Succeeded;
            }

            doc.Regenerate();   // apparent load + circuit number are computed on regen

            sb.AppendLine("CREATE #1 OK.");
            sb.AppendLine($"  Circuit Number  : \"{GetString(circuit, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)}\"  (expect \"<unnamed>\")");
            sb.AppendLine($"  Members         : {CountMembers(circuit)}");
            sb.AppendLine($"  Apparent load   : {LoadReport(circuit)}");
            sb.AppendLine($"  Sum of member fixture loads (indep.): {SumMemberFixtureLoads(doc, selIds):F2} VA");
            sb.AppendLine();

            // ── B. Second Create on the now-circuited fixtures: throws or null? ──────────────────
            try
            {
                var again = ElectricalSystem.Create(doc, selIds, ElectricalSystemType.PowerCircuit);
                sb.AppendLine(again == null
                    ? "CREATE #2 (already-circuited members) returned NULL — CircuitService's null path covers it."
                    : "CREATE #2 (already-circuited members) UNEXPECTEDLY returned a system (id "
                      + $"{again.Id}) — investigate; would double-circuit.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"CREATE #2 (already-circuited members) THREW: {ex.GetType().Name}: {ex.Message}");
                sb.AppendLine("→ CircuitService.CreateCircuit must CATCH this (it only handles the null return today).");
            }
            sb.AppendLine();

            // ── A. Teardown: doc.Delete(circuit.Id) — deletes the system + frees the fixtures? ──
            var fixtureIds = selIds.Where(id => IsLightingFixture(doc, id)).ToList();
            ICollection<ElementId> deleted;
            try
            {
                deleted = doc.Delete(circuit.Id);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"DELETE(circuit.Id) THREW: {ex.GetType().Name}: {ex.Message}");
                sb.AppendLine("→ doc.Delete(system.Id) is not the teardown path; try member deletion / DisconnectPanel.");
                tx.RollBack();
                Show(sb);
                return Result.Succeeded;
            }

            doc.Regenerate();
            sb.AppendLine($"DELETE(circuit.Id) OK — {deleted.Count} element(s) reported deleted.");

            int survived = 0, freed = 0, stillCircuited = 0, invalid = 0;
            foreach (var id in fixtureIds)
            {
                var el = doc.GetElement(id);
                if (el == null || !el.IsValidObject) { invalid++; continue; }
                survived++;
                if (el is FamilyInstance fi)
                {
                    var systems = fi.MEPModel?.GetElectricalSystems();
                    bool onCircuit = systems != null && systems.Cast<ElectricalSystem>().Any();
                    if (onCircuit) stillCircuited++; else freed++;
                }
            }
            sb.AppendLine($"  Fixtures after delete: survived={survived}, freed(un-circuited)={freed}, "
                        + $"stillCircuited={stillCircuited}, invalidated={invalid}  (of {fixtureIds.Count})");
            sb.AppendLine(freed == fixtureIds.Count && invalid == 0
                ? "→ CLEAN: doc.Delete(circuit.Id) removes the circuit and frees every fixture. Two-phase reconcile works."
                : "→ NOT clean — read the counts; reconcile teardown needs the member-deletion path instead.");
        }
        finally
        {
            if (tx.HasStarted()) tx.RollBack();   // never persist the probe's create/delete
        }

        Show(sb);
        return Result.Succeeded;
    }

    private static void Show(StringBuilder sb) =>
        new TaskDialog("TurboSpike — DMX circuit probe")
        {
            MainInstruction = "DMX per-decoder circuit feasibility",
            MainContent = sb.ToString(),
        }.Show();

    private static string DescribeSelection(Document doc, IEnumerable<ElementId> ids)
    {
        int fixtures = 0, devices = 0, other = 0;
        foreach (var id in ids)
        {
            var bic = doc.GetElement(id)?.Category?.BuiltInCategory;
            if (bic == BuiltInCategory.OST_LightingFixtures) fixtures++;
            else if (bic == BuiltInCategory.OST_LightingDevices) devices++;
            else other++;
        }
        return $"  Lighting fixtures={fixtures}, lighting devices={devices}, other={other}";
    }

    private static bool IsLightingFixture(Document doc, ElementId id) =>
        doc.GetElement(id)?.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures;

    private static int CountMembers(ElectricalSystem circuit)
    {
        int n = 0;
        foreach (Element _ in circuit.Elements) n++;
        return n;
    }

    private static string LoadReport(ElectricalSystem circuit)
    {
        var p = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
        if (p == null) return "(RBS_ELEC_APPARENT_LOAD not found)";
        return $"{p.AsDouble():F4} internal  |  \"{p.AsValueString()}\"";
    }

    // Independent per-fixture apparent-load sum, for comparison with the circuit total (device 0.00 VA check).
    private static double SumMemberFixtureLoads(Document doc, IEnumerable<ElementId> ids)
    {
        double sum = 0;
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is FamilyInstance fi &&
                fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures)
            {
                var p = fi.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
                if (p != null && p.StorageType == StorageType.Double) sum += p.AsDouble();
            }
        }
        return sum;
    }

    private static string GetString(Element e, BuiltInParameter bip) =>
        e.get_Parameter(bip)?.AsString() ?? "(null)";
}
