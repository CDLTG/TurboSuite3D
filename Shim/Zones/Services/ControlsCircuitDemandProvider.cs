#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Wireless keypads as a circuited subsystem (F2/F3) — the reader half of the plan where a hybrid
    /// repeater is an Electrical-Equipment "panel" and each wireless keypad is a member of a
    /// <c>Controls</c> electrical circuit on it. It mirrors <see cref="ShadeDemandProvider"/>: group by
    /// the circuit's panel name → <see cref="PanelAllocationService.ParseLocationNumber"/> (the
    /// location), and hand the per-location repeater + keypad tallies to the packer, which sizes Clear
    /// Connect links <b>per location</b> (<c>Σ_loc ceil(repeaters_loc / 4)</c>).
    ///
    /// <b>Two reads, merged by location.</b> Repeater counts come from the repeater <i>elements</i>
    /// (every repeater needs a link, even one with no keypad circuited yet), keyed by the repeater's
    /// own panel name (<c>{Location}-REP{N}</c>). The wireless keypads come from the <c>Controls</c>
    /// circuits, whose panel is the repeater, so the circuit's panel name resolves to the same
    /// location. A location is the base before the first dash, exactly like the shade "{Location}-{ID}".
    ///
    /// <b>Located vs unlocated mirrors shades.</b> A repeater whose panel name parses to a real
    /// location is sized and placed; one that does not is a warning (dropped from the located path,
    /// which falls back to global pooling), never a link — the same contract <c>ShadeSolver</c> holds
    /// for a shade with no SHADE panel. The packer applies that filter; this just reports what it reads.
    /// </summary>
    public sealed class ControlsCircuitDemandProvider
    {
        private const string Unassigned = "(unassigned)";

        /// <summary>Repeaters (and the wireless keypads circuited to them) totalled per location, in
        /// first-seen order. Feeds <c>BomExtras.RepeaterLocations</c> → the packer's per-location CC-A
        /// sizing. Read once on the API thread like the other Zones reads.</summary>
        public static List<RepeaterLocationTally> CollectLocations(Document doc)
        {
            if (doc == null) return new List<RepeaterLocationTally>();

            // Keyed by the PARSED location number, not the repeater's full panel name — so two
            // repeaters in one location ("2-REP1", "2-REP2") pool into one location (⇒ one CC-A link),
            // not two. The display name is the first panel name seen for that location.
            var repeatersByLocation = new Dictionary<int, int>();
            var keypadsByLocation = new Dictionary<int, List<KeypadRecord>>();
            var nameByLocation = new Dictionary<int, string>();
            var order = new List<int>();

            void Seen(int location, string name)
            {
                if (repeatersByLocation.ContainsKey(location)) return;
                repeatersByLocation[location] = 0;
                keypadsByLocation[location] = new List<KeypadRecord>();
                nameByLocation[location] = name;
                order.Add(location);
            }

            // 1) Every hybrid repeater, grouped by its own panel-name location — counts keypad-less
            //    repeaters too, since each still needs a Clear Connect link.
            var repeaters = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => string.Equals(ParameterHelper.GetRole(fi), Roles.HybridRepeater,
                    StringComparison.OrdinalIgnoreCase));

            foreach (var rep in repeaters)
            {
                string name = NameOrUnassigned(rep.Name);
                int location = PanelAllocationService.ParseLocationNumber(name);
                Seen(location, name);
                repeatersByLocation[location]++;
            }

            // 2) Wireless keypads via their Controls circuits — grouped by the circuit's panel (the
            //    repeater), which resolves to the same location.
            var regionFallback = new RegionRoomLookupService(doc);
            var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);

            var controlsCircuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.Controls);

            foreach (var circuit in controlsCircuits)
            {
                string name = NameOrUnassigned(ParameterHelper.GetPanelName(circuit));
                int location = PanelAllocationService.ParseLocationNumber(name);
                Seen(location, name);

                if (circuit.Elements == null) continue;
                foreach (Element el in circuit.Elements)
                {
                    if (el is not FamilyInstance fi) continue;
                    if (fi.Category?.BuiltInCategory != BuiltInCategory.OST_LightingDevices) continue;
                    if (!string.Equals(ParameterHelper.GetRole(fi), Roles.Keypad,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    Parameter? twoGang = fi.LookupParameter(ParameterNames.TwoGang)
                        ?? fi.Symbol?.LookupParameter(ParameterNames.TwoGang);
                    bool isTwoGang = twoGang != null && twoGang.AsInteger() == 1;

                    keypadsByLocation[location].Add(new KeypadRecord(
                        ParameterHelper.GetSwitchID(fi) ?? string.Empty,
                        ResolveRoomName(fi, roomCache),
                        fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString() ?? string.Empty,
                        location,
                        devices: isTwoGang ? 2 : 1));
                }
            }

            // Switch-ID order within each location (numbered first, then unnumbered), so the one-line's
            // repeater fan reads in order — the same ordering the shade motor list uses.
            var natural = new NaturalStringComparer();
            return order.Select(loc => new RepeaterLocationTally(
                nameByLocation[loc],
                loc,
                repeatersByLocation[loc],
                keypadsByLocation[loc]
                    .OrderBy(k => string.IsNullOrEmpty(k.SwitchId))
                    .ThenBy(k => k.SwitchId, natural)
                    .ToList())).ToList();
        }

        private static string NameOrUnassigned(string? name) =>
            string.IsNullOrWhiteSpace(name) ? Unassigned : name.Trim();

        private static string ResolveRoomName(FamilyInstance fi, SpaceRoomFinderService.SpaceLookupCache roomCache)
        {
            Space? space = roomCache.FindSpace(fi);
            if (space != null) return SpaceRoomFinderService.ReadSpaceName(space);
            return roomCache.FindRoomName(fi) ?? string.Empty;
        }
    }
}
