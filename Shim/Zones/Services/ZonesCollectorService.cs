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
                var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);

                // Persisted per-circuit room overrides (keyed by circuit UniqueId).
                var roomOverrides = RoomOverrideStorageService.Load(doc);

                foreach (ElectricalSystem circuit in circuits)
                {
                    try
                    {
                        string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        // A shade circuit carries shade motors (Electrical Fixtures) — which the fixture
                        // filter below would otherwise pull in as a lighting zone. The shade subsystem
                        // (ShadeDemandProvider) accounts for these separately, so drop them here. A no-op
                        // on any job without shade motors.
                        if (ShadeDemandProvider.IsShadeCircuit(circuit))
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

                        // Resolve the control-module type from the fixtures' Dimming Protocol
                        // (a schedule-visible type parameter) rather than the connector-level
                        // Load Classification Abbreviation this used to read.
                        var dimming = DimmingModuleResolver.Resolve(
                            fixtures.Select(fi => ParameterHelper.GetDimmingProtocol(fi)));

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
                            DimmingType = dimming.ModuleType,
                            DimmingProtocolDisplay = dimming.ProtocolDisplay,
                            DimmingOutcome = dimming.Outcome,
                            DimmingSubsystem = dimming.Subsystem,
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


        /// <summary>
        /// Keypads, split two ways: by gang (a two-gang keypad is two devices) and by radio.
        ///
        /// A wireless keypad rides the processor's Clear Connect link rather than a QS link, so it
        /// consumes a different link's device budget — which is why the two are counted apart rather
        /// than summed here. See <see cref="ParameterNames.Wireless"/>: absent reads as wired, which
        /// is the behaviour that shipped before the parameter existed.
        /// </summary>
        public KeypadCounts GetKeypadCounts(Document doc)
        {
            var counts = new KeypadCounts();

            var keypads = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    string familyName = fi.Symbol?.Family?.Name ?? "";
                    return familyName.IndexOf("keypad", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .ToList();

            counts.Tallies = TallyCatalogSlots(keypads);

            foreach (var fi in keypads)
            {
                Parameter twoGangParam = fi.LookupParameter(ParameterNames.TwoGang)
                    ?? fi.Symbol?.LookupParameter(ParameterNames.TwoGang);
                bool isTwoGang = twoGangParam != null && twoGangParam.AsInteger() == 1;

                if (IsWireless(fi))
                {
                    // Gang still doubles the device count — a two-gang wireless keypad is two devices
                    // on the Clear Connect link, same as it is two on a QS link.
                    counts.WirelessDevices += isTwoGang ? 2 : 1;
                }
                else if (isTwoGang)
                {
                    counts.TwoGang++;
                }
                else
                {
                    counts.Regular++;
                }
            }

            return counts;
        }

        /// <summary>Instance value wins where a family exposes one; otherwise the type's, since wired
        /// vs wireless is normally a property of the model. Absent ⇒ wired.</summary>
        private static bool IsWireless(FamilyInstance fi)
        {
            Parameter param = fi.LookupParameter(ParameterNames.Wireless)
                ?? fi.Symbol?.LookupParameter(ParameterNames.Wireless);
            return param != null && param.AsInteger() == 1;
        }

        /// <summary>
        /// Hybrid Repeaters — how many devices are on the link, and what to order for them.
        ///
        /// The part number used to be read off the <b>first instance only</b>, which ordered a
        /// two-model job as however many of whichever model happened to be collected first.
        /// </summary>
        public ControlDeviceGroup GetHybridRepeaters(Document doc)
        {
            var repeaters = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => string.Equals(fi.Symbol?.Family?.Name,
                    "AL_Electrical Fixture_Hybrid Repeater", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ControlDeviceGroup
            {
                DeviceCount = repeaters.Count,
                Tallies = TallyCatalogSlots(repeaters)
            };
        }

        /// <summary>
        /// Counts instances per family type, reads that type's six catalog slots once, and merges the
        /// rows across types by catalog number.
        ///
        /// The reading and the arithmetic are <see cref="CatalogSlotTally"/>'s, in Core, so the grammar's
        /// behaviour is pinned by tests. All this does is get the parameters off the symbol.
        /// </summary>
        private static List<ControlDeviceTally> TallyCatalogSlots(IEnumerable<FamilyInstance> instances)
        {
            var perSymbol = new Dictionary<ElementId, (FamilySymbol Symbol, int Count)>();
            foreach (var fi in instances)
            {
                var symbol = fi.Symbol;
                if (symbol == null) continue;

                if (perSymbol.TryGetValue(symbol.Id, out var entry))
                    perSymbol[symbol.Id] = (entry.Symbol, entry.Count + 1);
                else
                    perSymbol[symbol.Id] = (symbol, 1);
            }

            var rows = new List<ControlDeviceTally>();
            foreach (var (symbol, count) in perSymbol.Values)
            {
                var catalogNumbers = new string[CatalogSlotTally.SlotCount];
                var qtyTokens = new string[CatalogSlotTally.SlotCount];
                for (int slot = 0; slot < CatalogSlotTally.SlotCount; slot++)
                {
                    catalogNumbers[slot] =
                        symbol.LookupParameter($"Catalog Number{slot + 1}")?.AsString() ?? "";
                    qtyTokens[slot] =
                        symbol.LookupParameter($"Catalog Qty{slot + 1}")?.AsString() ?? "";
                }

                // A family has two description fields and six catalog slots, so they pair by position
                // and stop: Catalog Number1 takes the built-in Description, Catalog Number2 takes
                // Description2, and the rest carry none. No library type uses slots 3-6 today.
                var descriptions = new string[CatalogSlotTally.SlotCount];
                descriptions[0] = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)
                    ?.AsString() ?? "";
                descriptions[1] = symbol.LookupParameter(ParameterNames.Description2)
                    ?.AsString() ?? "";

                rows.AddRange(CatalogSlotTally.ForType(
                    symbol.Name ?? "", count, catalogNumbers, qtyTokens, descriptions));
            }

            return CatalogSlotTally.Merge(rows);
        }
    }
}
