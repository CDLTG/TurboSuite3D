#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
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
                var lightingCatId = new ElementId(BuiltInCategory.OST_LightingFixtures);
                var electricalCatId = new ElementId(BuiltInCategory.OST_ElectricalFixtures);

                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                    .Cast<ElectricalSystem>()
                    .ToList();

                var regionFallback = new RegionRoomLookupService(doc);
                var roomCache = new LinkedRoomFinderService.RoomLookupCache(doc, regionFallback);

                // Persisted per-circuit room overrides (keyed by circuit UniqueId).
                var roomOverrides = ZonesRoomOverrideStorageService.Load(doc);

                foreach (ElectricalSystem circuit in circuits)
                {
                    try
                    {
                        string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        // Get fixtures directly from the circuit's connected elements
                        // (avoids grouping by circuit number string, which fails when
                        // multiple circuits share the same number like "<unnamed>")
                        var fixtures = new List<FamilyInstance>();
                        bool hasSwitchElement = false;
                        foreach (Element el in circuit.Elements)
                        {
                            if (el is FamilyInstance fi)
                            {
                                if (fi.Category.Id == lightingCatId || fi.Category.Id == electricalCatId)
                                {
                                    fixtures.Add(fi);
                                    if (fi.Category.Id == electricalCatId)
                                    {
                                        string familyName = fi.Symbol?.Family?.Name ?? "";
                                        if (familyName.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0)
                                            hasSwitchElement = true;
                                    }
                                }
                            }
                        }
                        if (fixtures.Count == 0)
                            continue;

                        // Resolve dimming type from Load Classification Abbreviation
                        // If multiple values separated by semicolons (e.g. "ELV; T.B.D."), use the first
                        string dimmingType = ParameterHelper.GetLoadClassification(circuit);
                        if (!string.IsNullOrEmpty(dimmingType) && dimmingType.Contains(';'))
                            dimmingType = dimmingType.Substring(0, dimmingType.IndexOf(';')).Trim();

                        string currentLoadName = ParameterHelper.GetLoadName(circuit);

                        // Resolve room name from first fixture (falls back to region Comments in 2D)
                        string roomName = roomCache.FindRoomName(fixtures[0]);

                        // Persisted override for this circuit, if any.
                        roomOverrides.TryGetValue(circuit.UniqueId, out string roomOverride);

                        string circuitComments = ParameterHelper.GetCircuitComments(circuit);

                        string fixtureComments = string.Join(", ",
                            fixtures
                                .Select(fi => ParameterHelper.GetComments(fi))
                                .Where(c => !string.IsNullOrWhiteSpace(c))
                                .Distinct());

                        string loadClassificationName = ParameterHelper.GetLoadClassificationName(circuit);

                        // Resolve load name label using priority order
                        string label = ZonesLabelResolver.ResolveLabel(circuitComments, fixtureComments, loadClassificationName, out LabelSource labelSource);
                        // Override takes priority over the resolved room name (matches the VM).
                        string room = !string.IsNullOrWhiteSpace(roomOverride) ? roomOverride : roomName;
                        string updatedLoadName = string.Empty;
                        if (!string.IsNullOrWhiteSpace(room) && !string.IsNullOrWhiteSpace(label))
                            updatedLoadName = $"{room.ToUpperInvariant()} - {label.ToLowerInvariant()}";
                        else
                            labelSource = LabelSource.None;

                        string panelName = ParameterHelper.GetPanelName(circuit);

                        result.Add(new ZonesCircuitData
                        {
                            CircuitId = circuit.Id.ToRef(),
                            CircuitNumber = circuitNumber,
                            DimmingType = dimmingType ?? string.Empty,
                            PanelName = panelName ?? string.Empty,
                            RoomName = roomName ?? string.Empty,
                            RoomOverride = roomOverride ?? string.Empty,
                            CurrentLoadName = currentLoadName ?? string.Empty,
                            CircuitComments = circuitComments ?? string.Empty,
                            FixtureComments = fixtureComments ?? string.Empty,
                            LoadClassificationName = loadClassificationName ?? string.Empty,
                            UpdatedLoadName = updatedLoadName,
                            LabelSource = labelSource,
                            IsWiredToSwitch = hasSwitchElement,
                            ApparentLoadVA = ParameterHelper.GetApparentLoad(circuit)
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
                Parameter twoGangParam = fi.LookupParameter(ParameterNames.TwoGang)
                    ?? fi.Symbol?.LookupParameter(ParameterNames.TwoGang);
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

            string partNumber = repeaters[0].Symbol?.LookupParameter(ParameterNames.CatalogNumber1)?.AsString();
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

                string catalogNumber = panel.Symbol?.LookupParameter(ParameterNames.CatalogNumber1)?.AsString();
                if (!string.IsNullOrEmpty(catalogNumber))
                    result[name] = catalogNumber;
            }

            return result;
        }

    }
}
