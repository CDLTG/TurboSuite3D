using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Command-neutral electrical-circuit primitives: analyze fixtures, create a circuit with
/// the remembered panel default, add fixtures, read/write comments, and set/clear the panel.
/// Shared by every command that creates or edits circuits (TurboWire, TurboDriver, …) so the
/// panel-default rule and the lighting/electrical-only fixture filter have a single
/// implementation. (Formerly <c>TurboSuite.Wire.Services.CircuitService</c>.)
/// </summary>
public static class CircuitService
{
    public class CircuitAnalysis
    {
        public List<FamilyInstance> CircuitedFixtures { get; } = new();
        public List<FamilyInstance> UncircuitedFixtures { get; } = new();
        public Dictionary<ElementId, ElectricalSystem> CircuitMap { get; } = new();

        public bool AllUncircuited => CircuitMap.Count == 0;
        public bool SingleCircuit => CircuitMap.Count == 1;
        public bool MultipleCircuits => CircuitMap.Count > 1;
        public ElectricalSystem? SingleCircuitRef => SingleCircuit ? CircuitMap.Values.First() : null;
    }

    /// <summary>
    /// Analyze fixtures to determine their circuit state.
    /// </summary>
    public static CircuitAnalysis AnalyzeFixtures(List<FamilyInstance> fixtures)
    {
        var analysis = new CircuitAnalysis();

        foreach (var fixture in fixtures)
        {
            var systems = fixture.MEPModel?.GetElectricalSystems();
            ElectricalSystem? es = null;
            if (systems != null)
            {
                foreach (ElectricalSystem s in systems)
                {
                    es = s;
                    break;
                }
            }

            if (es != null)
            {
                analysis.CircuitedFixtures.Add(fixture);
                analysis.CircuitMap[es.Id] = es;
            }
            else
            {
                analysis.UncircuitedFixtures.Add(fixture);
            }
        }

        return analysis;
    }

    /// <summary>
    /// The lighting/electrical fixtures on a circuit, in circuit-member order. Deliberately
    /// excludes lighting <b>devices</b> (power supplies, decoders) so room resolution and
    /// counts never key off a device that TurboDriver placed <i>outside</i> the fixtures'
    /// room. Any circuit code that needs "the fixtures" must go through here.
    /// </summary>
    public static List<FamilyInstance> GetFixturesOnCircuit(ElectricalSystem circuit)
    {
        var fixtures = new List<FamilyInstance>();
        foreach (Element element in circuit.Elements)
        {
            if (element is FamilyInstance fi &&
                (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures ||
                 fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures))
            {
                fixtures.Add(fi);
            }
        }
        return fixtures;
    }

    /// <summary>
    /// Create a new electrical circuit from the given fixtures and assign it to the
    /// most recently used panel in the document (matching Revit's default UI behavior).
    /// <paramref name="shadePanels"/> switches the remembered default to the last shade
    /// (35 V) location — used by TurboWire's one-shade-per-circuit shade mode.
    /// <paramref name="preprocessor"/> is an optional failure preprocessor for the create
    /// transaction — TurboDMX passes one to swallow the expected over-amp warning on its
    /// intentionally-overpacked zone circuits; other callers leave it null.
    /// </summary>
    public static ElectricalSystem? CreateCircuit(Document doc, List<FamilyInstance> fixtures,
        bool assignPanel = true, bool shadePanels = false, IFailuresPreprocessor? preprocessor = null)
    {
        using var t = new Transaction(doc, "Create circuit");
        if (preprocessor != null)
        {
            var opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(preprocessor);
            t.SetFailureHandlingOptions(opts);
        }
        t.Start();

        var fixtureIds = fixtures.Select(f => f.Id).ToList();
        var circuit = ElectricalSystem.Create(doc, fixtureIds, ElectricalSystemType.PowerCircuit);
        if (circuit == null)
        {
            t.RollBack();
            return null;
        }

        if (assignPanel)
        {
            // Mirror the last circuit's assignment (exclude the one we just created so it
            // doesn't answer for itself). A deliberate <None> last time leaves this one
            // unassigned too; the info dialog then defaults to <None> to match.
            var (lastPanel, preferNone) = FindLastPanelChoice(doc, new[] { circuit.Id }, shadePanels);
            if (!preferNone && lastPanel != null)
            {
                try { circuit.SelectPanel(lastPanel); }
                catch { /* Panel may be incompatible — leave unassigned */ }
            }
        }

        t.Commit();
        return circuit;
    }

    /// <summary>
    /// Get the electrical panels a lighting/power circuit can be assigned to, sorted by name.
    /// Shade/control panels (on the 35 V distribution system) are excluded — a lighting circuit
    /// cannot live on them, and they must not appear in the TurboWire/TurboDriver panel picker.
    /// See <see cref="PanelClassifier"/>.
    /// </summary>
    public static List<FamilyInstance> GetAllPanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(IsLightingPanel)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Get the shade/control panels (35 V "locations") in the document, sorted by name — the
    /// inverse of <see cref="GetAllPanels"/>. This is the picker source for TurboWire's shade
    /// mode, where a shade is circuited onto a shade location. See <see cref="PanelClassifier"/>.
    /// </summary>
    public static List<FamilyInstance> GetShadePanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(IsShadePanel)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when a lighting/power circuit may be assigned to this panel (not a 35 V
    /// shade/control panel). Reads the panel's downstream distribution system.</summary>
    private static bool IsLightingPanel(FamilyInstance panel) =>
        PanelClassifier.IsLightingPanel(ParameterHelper.GetPanelDistributionSystemName(panel));

    /// <summary>True when this panel is a 35 V shade/control location.</summary>
    private static bool IsShadePanel(FamilyInstance panel) =>
        PanelClassifier.IsShadePanel(ParameterHelper.GetPanelDistributionSystemName(panel));

    /// <summary>
    /// Assign a circuit to a specific panel.
    /// </summary>
    public static void SetCircuitPanel(Document doc, ElectricalSystem circuit, FamilyInstance panel)
    {
        using var t = new Transaction(doc, "Set circuit panel");
        t.Start();
        try
        {
            circuit.SelectPanel(panel);
            t.Commit();
        }
        catch
        {
            t.RollBack();
        }
    }

    /// <summary>
    /// Unassign a circuit from its panel (e.g. DMX/DALI circuits that never live on a
    /// distribution board). No-op-safe: swallows if the circuit has no panel.
    /// </summary>
    public static void ClearCircuitPanel(Document doc, ElectricalSystem circuit)
    {
        using var t = new Transaction(doc, "Unassign circuit panel");
        t.Start();
        try
        {
            circuit.DisconnectPanel();
            t.Commit();
        }
        catch
        {
            t.RollBack();
        }
    }

    /// <summary>
    /// The panel default for a newly wired circuit, mirroring the most recent circuit
    /// the user set up (highest ElementId) — Revit's "last selected panel" behavior,
    /// extended to remember a deliberate &lt;None&gt;:
    /// <list type="bullet">
    /// <item><description><c>(panel, false)</c> — the newest circuit is on a panel.</description></item>
    /// <item><description><c>(null, true)</c> — the newest circuit was left unassigned
    /// (DMX/DALI etc.); default the next one to &lt;None&gt; too.</description></item>
    /// <item><description><c>(null, false)</c> — nothing to go on yet; caller picks its
    /// own default (first available panel).</description></item>
    /// </list>
    /// "Switched" circuits are skipped — they are unassigned by design (no dialog) and
    /// must not poison the panel that regular wiring remembers. Circuits on the <em>other</em>
    /// kind of panel are also skipped, keyed by <paramref name="shadePanels"/>: lighting wiring
    /// (default) ignores circuits on shade/control (35 V) panels, and shade mode ignores circuits
    /// on lighting panels — so each remembers only its own last location. <paramref name="exclude"/>
    /// omits circuits already being wired in the current run so they don't answer for
    /// themselves.
    /// </summary>
    public static (FamilyInstance? Panel, bool PreferNone) FindLastPanelChoice(
        Document doc, ICollection<ElementId>? exclude = null, bool shadePanels = false)
    {
        var newest = new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
            .Cast<ElectricalSystem>()
            .Where(c => (exclude == null || !exclude.Contains(c.Id)) && !IsSwitchedCircuit(c)
                        && MatchesPanelKind(c, shadePanels))
            .OrderByDescending(c => c.Id.Value)
            .FirstOrDefault();

        if (newest == null)
            return (null, false);
        return newest.BaseEquipment is FamilyInstance panel ? (panel, false) : (null, true);
    }

    /// <summary>Whether a circuit belongs to the panel kind being remembered. A circuit on a
    /// panel counts only if that panel is the requested kind (shade vs. lighting); an unassigned
    /// circuit counts for either (it answers the deliberate-&lt;None&gt; question). So lighting
    /// wiring skips shade-panel circuits and vice versa.</summary>
    private static bool MatchesPanelKind(ElectricalSystem circuit, bool shadePanels) =>
        circuit.BaseEquipment is not FamilyInstance panel || IsShadePanel(panel) == shadePanels;

    private static bool IsSwitchedCircuit(ElectricalSystem circuit) =>
        string.Equals(ParameterHelper.GetCircuitComments(circuit), "switched",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Add uncircuited fixtures to an existing circuit.
    /// </summary>
    public static void AddFixturesToCircuit(Document doc, ElectricalSystem circuit, List<FamilyInstance> fixtures)
    {
        if (fixtures.Count == 0) return;

        using var t = new Transaction(doc, "Add fixtures to circuit");
        t.Start();

        var addSet = new ElementSet();
        foreach (var fi in fixtures)
            addSet.Insert(fi);
        circuit.AddToCircuit(addSet);

        t.Commit();
    }

    /// <summary>
    /// Set the Comments parameter on a circuit.
    /// </summary>
    public static void SetCircuitComments(Document doc, ElectricalSystem circuit, string comments)
    {
        using var t = new Transaction(doc, "Set circuit comment");
        t.Start();

        var param = circuit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        param?.Set(comments);

        t.Commit();
    }

    /// <summary>
    /// Collect all unique non-empty circuit comments in the document, sorted alphabetically.
    /// </summary>
    public static List<string> GetExistingComments(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
            .Cast<ElectricalSystem>()
            .Select(c => ParameterHelper.GetCircuitComments(c))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
