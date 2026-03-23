using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Wire.Services;

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
    /// Create a new electrical circuit from the given fixtures and assign it to the
    /// most recently used panel in the document (matching Revit's default UI behavior).
    /// </summary>
    public static ElectricalSystem? CreateCircuit(Document doc, List<FamilyInstance> fixtures, bool assignPanel = true)
    {
        using var t = new Transaction(doc, "TurboWire — Create circuit");
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
            // Assign to the most recently used panel (highest circuit number)
            var lastPanel = FindLastUsedPanel(doc);
            if (lastPanel != null)
            {
                try { circuit.SelectPanel(lastPanel); }
                catch { /* Panel may be incompatible — leave unassigned */ }
            }
        }

        t.Commit();
        return circuit;
    }

    /// <summary>
    /// Get all electrical panels (distribution boards) in the document, sorted by name.
    /// </summary>
    public static List<FamilyInstance> GetAllPanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Assign a circuit to a specific panel.
    /// </summary>
    public static void SetCircuitPanel(Document doc, ElectricalSystem circuit, FamilyInstance panel)
    {
        using var t = new Transaction(doc, "TurboWire — Set circuit panel");
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
    /// Find the panel used by the most recently created circuit in the document
    /// (highest ElementId), approximating Revit's "last selected panel" behavior.
    /// </summary>
    public static FamilyInstance? FindLastUsedPanel(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
            .Cast<ElectricalSystem>()
            .Where(c => c.BaseEquipment != null)
            .OrderByDescending(c => c.Id.Value)
            .FirstOrDefault()
            ?.BaseEquipment;
    }

    /// <summary>
    /// Add uncircuited fixtures to an existing circuit.
    /// </summary>
    public static void AddFixturesToCircuit(Document doc, ElectricalSystem circuit, List<FamilyInstance> fixtures)
    {
        if (fixtures.Count == 0) return;

        using var t = new Transaction(doc, "TurboWire — Add fixtures to circuit");
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
        using var t = new Transaction(doc, "TurboWire — Set circuit comment");
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
