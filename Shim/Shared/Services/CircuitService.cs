using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Which family of panel a circuit lives on — the single axis the picker, the create system-type,
/// and the "remember my last panel" rule all key off. Three kinds: <see cref="Lighting"/> (a normal
/// power circuit on a distribution board), <see cref="Shade"/> (a power circuit on a 35 V shade
/// location), and <see cref="Control"/> (a <c>Controls</c> circuit on a hybrid-repeater panel —
/// wireless keypad → repeater). Replaces the former two-state <c>bool shadePanels</c>.
/// </summary>
public enum PanelKind
{
    Lighting,
    Shade,
    Control
}

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
    /// <paramref name="kind"/> picks both the circuit's system type (<see cref="PanelKind.Control"/>
    /// → a <c>Controls</c> circuit, all others → <c>PowerCircuit</c>) and the remembered-default
    /// panel family (shade 35 V location / hybrid-repeater panel / lighting board).
    /// <paramref name="preprocessor"/> is an optional failure preprocessor for the create
    /// transaction — TurboDMX passes one to swallow the expected over-amp warning on its
    /// intentionally-overpacked zone circuits; other callers leave it null.
    /// </summary>
    public static ElectricalSystem? CreateCircuit(Document doc, List<FamilyInstance> fixtures,
        bool assignPanel = true, PanelKind kind = PanelKind.Lighting, IFailuresPreprocessor? preprocessor = null)
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
        // A keypad→repeater relationship is a Controls circuit (no load); everything else is power.
        // Spike-verified (Revit 2025): ElectricalSystem.Create accepts Controls without throwing.
        var systemType = kind == PanelKind.Control
            ? ElectricalSystemType.Controls
            : ElectricalSystemType.PowerCircuit;
        var circuit = ElectricalSystem.Create(doc, fixtureIds, systemType);
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
            var (lastPanel, preferNone) = FindLastPanelChoice(doc, new[] { circuit.Id }, kind);
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
    /// cannot live on them — and so are hybrid-repeater panels (<see cref="PanelKind.Control"/>,
    /// identified by Role): their only connector is a <c>Controls</c> one, so a lighting circuit
    /// physically can't attach, but a repeater carries no distribution system (fail-open would
    /// otherwise leak it into the lighting picker). See <see cref="PanelClassifier"/>.
    /// </summary>
    public static List<FamilyInstance> GetAllPanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(p => IsLightingPanel(p) && !IsRepeaterPanel(p))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Get the shade panels (35 V "locations") in the document, sorted by name. This is the picker
    /// source for TurboWire's shade mode, where a shade is circuited onto a shade location. See
    /// <see cref="PanelClassifier"/>. (A repeater panel is not on 35 V, so it never appears here.)
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

    /// <summary>
    /// Get the hybrid-repeater "Control panels" (Electrical Equipment with Role=HybridRepeater),
    /// sorted by name — the picker source for TurboWire's keypad mode, where a wireless keypad is
    /// circuited onto a repeater. Identity is by Role (not distribution system): the repeater's
    /// Controls-only connector already blocks lighting/shade circuits physically, so it needs no
    /// distribution-system tag. See <c>TurboSuite.Shared.Constants.Roles.HybridRepeater</c>.
    /// </summary>
    public static List<FamilyInstance> GetRepeaterPanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(IsRepeaterPanel)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The picker source for a given panel kind.</summary>
    public static List<FamilyInstance> GetPanelsFor(Document doc, PanelKind kind) => kind switch
    {
        PanelKind.Shade => GetShadePanels(doc),
        PanelKind.Control => GetRepeaterPanels(doc),
        _ => GetAllPanels(doc)
    };

    /// <summary>True when a lighting/power circuit may be assigned to this panel (not a 35 V
    /// shade/control panel). Reads the panel's downstream distribution system.</summary>
    private static bool IsLightingPanel(FamilyInstance panel) =>
        PanelClassifier.IsLightingPanel(ParameterHelper.GetPanelDistributionSystemName(panel));

    /// <summary>True when this panel is a 35 V shade location.</summary>
    private static bool IsShadePanel(FamilyInstance panel) =>
        PanelClassifier.IsShadePanel(ParameterHelper.GetPanelDistributionSystemName(panel));

    /// <summary>True when this panel is a hybrid-repeater Control panel (Role=HybridRepeater).</summary>
    private static bool IsRepeaterPanel(FamilyInstance panel) =>
        string.Equals(ParameterHelper.GetRole(panel), Roles.HybridRepeater,
            StringComparison.OrdinalIgnoreCase);

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
    /// must not poison the panel that regular wiring remembers. Circuits on another <em>kind</em>
    /// of panel are also skipped, keyed by <paramref name="kind"/>: lighting wiring ignores
    /// circuits on shade (35 V) and repeater panels, shade mode ignores lighting/repeater, and
    /// keypad (control) mode ignores lighting/shade — so each remembers only its own last location.
    /// <paramref name="exclude"/> omits circuits already being wired in the current run so they
    /// don't answer for themselves.
    /// </summary>
    public static (FamilyInstance? Panel, bool PreferNone) FindLastPanelChoice(
        Document doc, ICollection<ElementId>? exclude = null, PanelKind kind = PanelKind.Lighting)
    {
        var newest = new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
            .Cast<ElectricalSystem>()
            .Where(c => (exclude == null || !exclude.Contains(c.Id)) && !IsSwitchedCircuit(c)
                        && MatchesPanelKind(c, kind))
            .OrderByDescending(c => c.Id.Value)
            .FirstOrDefault();

        if (newest == null)
            return (null, false);
        return newest.BaseEquipment is FamilyInstance panel ? (panel, false) : (null, true);
    }

    /// <summary>Whether a circuit belongs to the panel kind being remembered. A circuit on a
    /// panel counts only if that panel is the requested kind (lighting / shade / repeater); an
    /// unassigned circuit counts for any kind (it answers the deliberate-&lt;None&gt; question).
    /// So each mode skips the other two kinds' circuits.</summary>
    private static bool MatchesPanelKind(ElectricalSystem circuit, PanelKind kind)
    {
        if (circuit.BaseEquipment is not FamilyInstance panel) return true; // unassigned → any kind
        return kind switch
        {
            PanelKind.Shade => IsShadePanel(panel),
            PanelKind.Control => IsRepeaterPanel(panel),
            _ => IsLightingPanel(panel) && !IsRepeaterPanel(panel)
        };
    }

    /// <summary>Whether this is a TurboWire "switched" circuit — a local switch leg that stays
    /// unpaneled (&lt;unnamed&gt;) by design, stamped with the "switched" circuit comment at
    /// creation. Deliberately unpaneled, so consumers that surface forgotten circuits skip it.</summary>
    public static bool IsSwitchedCircuit(ElectricalSystem circuit) =>
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
