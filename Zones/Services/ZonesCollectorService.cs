#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public class ZonesCollectorService
    {
        public List<ZonesCircuitData> GetCircuits(Document doc)
        {
            var result = new List<ZonesCircuitData>();

            try
            {
                // Collect all lighting and electrical fixtures grouped by circuit number
                var fixturesByCircuit = new Dictionary<string, List<FamilyInstance>>();
                var categories = new[]
                {
                    BuiltInCategory.OST_LightingFixtures,
                    BuiltInCategory.OST_ElectricalFixtures
                };

                foreach (var category in categories)
                {
                    var collector = new FilteredElementCollector(doc)
                        .OfCategory(category)
                        .OfClass(typeof(FamilyInstance));

                    foreach (FamilyInstance fixture in collector)
                    {
                        try
                        {
                            Parameter circuitParam = fixture.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                            string circuitNumber = circuitParam?.AsString();
                            if (string.IsNullOrWhiteSpace(circuitNumber))
                                continue;

                            if (!fixturesByCircuit.ContainsKey(circuitNumber))
                                fixturesByCircuit[circuitNumber] = new List<FamilyInstance>();
                            fixturesByCircuit[circuitNumber].Add(fixture);
                        }
                        catch { continue; }
                    }
                }

                // Collect all electrical circuits
                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                    .Cast<ElectricalSystem>()
                    .ToList();

                foreach (ElectricalSystem circuit in circuits)
                {
                    try
                    {
                        string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        // Only include circuits that have lighting or electrical fixtures
                        if (!fixturesByCircuit.ContainsKey(circuitNumber))
                            continue;

                        var fixtures = fixturesByCircuit[circuitNumber];
                        if (fixtures.Count == 0)
                            continue;

                        // Resolve dimming type from Load Classification Abbreviation
                        // If multiple values separated by semicolons (e.g. "ELV; T.B.D."), use the first
                        string dimmingType = ParameterHelper.GetLoadClassification(circuit);
                        if (!string.IsNullOrEmpty(dimmingType) && dimmingType.Contains(';'))
                            dimmingType = dimmingType.Substring(0, dimmingType.IndexOf(';')).Trim();

                        // Read current Load Name from Revit
                        string currentLoadName = ParameterHelper.GetLoadName(circuit);

                        // Resolve room name from first fixture
                        string roomName = LinkedRoomFinderService.FindRoomName(doc, fixtures[0]);

                        // Read intermediate label sources
                        string circuitComments = ParameterHelper.GetCircuitComments(circuit);

                        string fixtureComments = string.Join(", ",
                            fixtures
                                .Select(fi => ParameterHelper.GetComments(fi))
                                .Where(c => !string.IsNullOrWhiteSpace(c))
                                .Distinct());

                        string loadClassificationName = ParameterHelper.GetLoadClassificationName(circuit);

                        // Resolve load name label using priority order
                        string label = ResolveLabel(circuitComments, fixtureComments, loadClassificationName, out LabelSource labelSource);
                        string updatedLoadName = string.Empty;
                        if (!string.IsNullOrWhiteSpace(roomName) && !string.IsNullOrWhiteSpace(label))
                            updatedLoadName = $"{roomName.ToUpperInvariant()} - {label.ToLowerInvariant()}";
                        else
                            labelSource = LabelSource.None;

                        string panelName = ParameterHelper.GetPanelName(circuit);

                        result.Add(new ZonesCircuitData
                        {
                            CircuitId = circuit.Id,
                            CircuitNumber = circuitNumber,
                            DimmingType = dimmingType ?? string.Empty,
                            PanelName = panelName ?? string.Empty,
                            RoomName = roomName ?? string.Empty,
                            CurrentLoadName = currentLoadName ?? string.Empty,
                            CircuitComments = circuitComments ?? string.Empty,
                            FixtureComments = fixtureComments ?? string.Empty,
                            LoadClassificationName = loadClassificationName ?? string.Empty,
                            UpdatedLoadName = updatedLoadName,
                            LabelSource = labelSource
                        });
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error",
                    $"Error collecting circuits:\n{ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Resolves the label portion of a load name from pre-read values.
        /// Priority: circuit comments > fixture comments > load classification name.
        /// Strips parenthetical content from the result.
        /// </summary>
        public static string ResolveLabel(string circuitComments, string fixtureComments, string loadClassificationName, out LabelSource source)
        {
            // Priority 1: Circuit Comments
            if (!string.IsNullOrWhiteSpace(circuitComments))
            {
                source = LabelSource.CircuitComments;
                return StripParenthetical(circuitComments);
            }

            // Priority 2: Fixture Comments (unique, joined)
            if (!string.IsNullOrWhiteSpace(fixtureComments))
            {
                source = LabelSource.FixtureComments;
                return StripParenthetical(fixtureComments);
            }

            // Priority 3: Load Classification (full name)
            if (!string.IsNullOrWhiteSpace(loadClassificationName))
            {
                source = LabelSource.Fallback;
                return StripParenthetical(loadClassificationName);
            }

            source = LabelSource.None;
            return string.Empty;
        }

        public (int regular, int twoGang) GetKeypadCounts(Document doc)
        {
            int regular = 0;
            int twoGang = 0;

            var keypads = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    string familyName = fi.Symbol?.Family?.Name ?? "";
                    return familyName.IndexOf("keypad", StringComparison.OrdinalIgnoreCase) >= 0;
                });

            foreach (var fi in keypads)
            {
                Parameter twoGangParam = fi.Symbol?.LookupParameter("Two Gang");
                if (twoGangParam != null && twoGangParam.AsInteger() == 1)
                    twoGang++;
                else
                    regular++;
            }

            return (regular, twoGang);
        }

        public (int count, string partNumber) GetHybridRepeaterInfo(Document doc)
        {
            var repeaters = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => string.Equals(fi.Symbol?.Family?.Name,
                    "AL_Electrical Fixture_Hybrid Repeater", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (repeaters.Count == 0)
                return (0, null);

            string partNumber = repeaters[0].Symbol?.LookupParameter("Catalog Number1")?.AsString();
            return (repeaters.Count, partNumber);
        }

        public Dictionary<string, string> GetPanelCatalogNumbers(Document doc)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var panel in panels)
            {
                string name = panel.Name;
                if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name))
                    continue;

                string catalogNumber = panel.Symbol?.LookupParameter("Catalog Number1")?.AsString();
                if (!string.IsNullOrEmpty(catalogNumber))
                    result[name] = catalogNumber;
            }

            return result;
        }

        private static string StripParenthetical(string label)
        {
            int parenIdx = label.IndexOf('(');
            if (parenIdx >= 0)
                label = label.Substring(0, parenIdx).TrimEnd();
            return label ?? string.Empty;
        }
    }
}
