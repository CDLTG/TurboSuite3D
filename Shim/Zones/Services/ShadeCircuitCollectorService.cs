#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// The shade twin of <see cref="ZonesCollectorService"/>: reads shade circuits (the ones the
    /// lighting collector deliberately <i>drops</i>) into <see cref="ZonesCircuitData"/> for the
    /// TurboZones Shade Names tab and the TurboDocs shade schedule.
    ///
    /// <b>Circuit = output.</b> A shade circuit carries exactly one shade motor by convention — one
    /// motor represents a whole multi-motor shade system — so each circuit is one row and one output
    /// on a QSPS-10PNL, the same one-circuit-one-name model lighting uses. The motor is the room
    /// anchor.
    ///
    /// Naming is identical to lighting on purpose: label = circuit comments → fixture comments →
    /// load classification (<see cref="ZonesLabelResolver"/>), room = owned Space (region fallback in
    /// 2D) with a persisted override winning, composed <c>ROOM - comment</c>. The only divergence is
    /// the override store: shade overrides live in <see cref="ShadeRoomOverrideStorageService"/> so a
    /// Shade-Names Apply and a Load-Names Apply never prune each other (see the store's remarks).
    /// Dimming/module fields are left blank — a QS motor has no dimming protocol, and the shade
    /// schedule shows none.
    /// </summary>
    public class ShadeCircuitCollectorService
    {
        public List<ZonesCircuitData> GetShadeCircuits(Document doc)
        {
            var result = new List<ZonesCircuitData>();

            try
            {
                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                    .Cast<ElectricalSystem>()
                    .ToList();

                var regionFallback = new RegionRoomLookupService(doc);
                var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);

                // Persisted per-circuit shade room overrides — the shade store, NOT the lighting one.
                var roomOverrides = ShadeRoomOverrideStorageService.Load(doc);

                foreach (ElectricalSystem circuit in circuits)
                {
                    try
                    {
                        // Only shade circuits — the mirror of the lighting collector's drop.
                        if (!ShadeDemandProvider.IsShadeCircuit(circuit))
                            continue;

                        string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        // The shade motor(s) on this circuit. One by convention, but collect all so
                        // room/comments resolve off a real motor even if the model has more than one.
                        var motors = circuit.Elements
                            .Cast<Element>()
                            .OfType<FamilyInstance>()
                            .Where(ShadeDemandProvider.IsShadeMotor)
                            .ToList();
                        if (motors.Count == 0)
                            continue;

                        string currentLoadName = ParameterHelper.GetLoadName(circuit);
                        string roomName = roomCache.FindRoomName(motors[0]);
                        roomOverrides.TryGetValue(circuit.UniqueId, out string roomOverride);

                        string circuitComments = ParameterHelper.GetCircuitComments(circuit);
                        string fixtureComments = string.Join(", ",
                            motors
                                .Select(fi => ParameterHelper.GetComments(fi))
                                .Where(c => !string.IsNullOrWhiteSpace(c))
                                .Distinct());
                        string loadClassificationName = ParameterHelper.GetLoadClassificationName(circuit);

                        string label = ZonesLabelResolver.ResolveLabel(
                            circuitComments, fixtureComments, loadClassificationName, out LabelSource labelSource);
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
                            PanelName = panelName ?? string.Empty,
                            RoomName = roomName ?? string.Empty,
                            RoomOverride = roomOverride ?? string.Empty,
                            CurrentLoadName = currentLoadName ?? string.Empty,
                            CircuitComments = circuitComments ?? string.Empty,
                            FixtureComments = fixtureComments ?? string.Empty,
                            LoadClassificationName = loadClassificationName ?? string.Empty,
                            UpdatedLoadName = updatedLoadName,
                            LabelSource = labelSource
                            // Dimming/subsystem/load fields intentionally left default — shades carry none.
                        });
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error",
                    $"Error collecting shade circuits:\n{ex.Message}");
            }

            return result;
        }
    }
}
