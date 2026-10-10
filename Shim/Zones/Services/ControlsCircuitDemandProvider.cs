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

            // Keyed by the repeater's PANEL NAME ("1-REP1"), so each repeater keeps its OWN keypads for the
            // one-line fan (F4). Locations pool by parsed number only when the tallies are grouped at the
            // end — two repeaters in one location ("2-REP1", "2-REP2") stay distinct records under one
            // location tally (still one CC-A link, two stamps).
            var repeaterByName = new Dictionary<string, RepeaterBuilder>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            RepeaterBuilder Seen(string name)
            {
                if (!repeaterByName.TryGetValue(name, out var b))
                {
                    b = new RepeaterBuilder(name, PanelAllocationService.ParseLocationNumber(name));
                    repeaterByName[name] = b;
                    order.Add(name);
                }
                return b;
            }

            // 1) Every hybrid repeater — one record each, keyed by its panel name; keypad-less repeaters
            //    still get a record, since each needs a Clear Connect link.
            var repeaters = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => string.Equals(ParameterHelper.GetRole(fi), Roles.HybridRepeater,
                    StringComparison.OrdinalIgnoreCase));

            foreach (var rep in repeaters)
                Seen(NameOrUnassigned(rep.Name));

            // 2) Wireless keypads via their Controls circuits — attached to the repeater the circuit's
            //    panel names. A circuit whose panel is no collected repeater still seeds a record, so its
            //    keypads are not lost.
            var regionFallback = new RegionRoomLookupService(doc);
            var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);

            var controlsCircuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.Controls);

            foreach (var circuit in controlsCircuits)
            {
                var builder = Seen(NameOrUnassigned(ParameterHelper.GetPanelName(circuit)));

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

                    builder.Keypads.Add(new KeypadRecord(
                        ParameterHelper.GetSwitchID(fi) ?? string.Empty,
                        ResolveRoomName(fi, roomCache),
                        fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString() ?? string.Empty,
                        builder.Location,
                        devices: isTwoGang ? 2 : 1));
                }
            }

            // Group the repeater records by location (first-seen order), each record's keypads in Switch-ID
            // order — the order the one-line's per-repeater fan reads, matching the shade motor list.
            var natural = new NaturalStringComparer();
            RepeaterRecord Build(RepeaterBuilder b) => new RepeaterRecord(
                b.Name, b.Location,
                b.Keypads
                    .OrderBy(k => string.IsNullOrEmpty(k.SwitchId))
                    .ThenBy(k => k.SwitchId, natural)
                    .ToList());

            var result = new List<RepeaterLocationTally>();
            var locOrder = new List<int>();
            var byLocation = new Dictionary<int, (string Name, List<RepeaterRecord> Reps)>();
            foreach (string name in order)
            {
                var b = repeaterByName[name];
                if (!byLocation.TryGetValue(b.Location, out var bucket))
                {
                    bucket = (name, new List<RepeaterRecord>());
                    byLocation[b.Location] = bucket;
                    locOrder.Add(b.Location);
                }
                bucket.Reps.Add(Build(b));
            }
            foreach (int loc in locOrder)
                result.Add(new RepeaterLocationTally(byLocation[loc].Name, loc, byLocation[loc].Reps));
            return result;
        }

        /// <summary>Accumulates one repeater's keypads while collecting, before it is frozen into a
        /// <see cref="RepeaterRecord"/>.</summary>
        private sealed class RepeaterBuilder
        {
            public RepeaterBuilder(string name, int location) { Name = name; Location = location; }
            public string Name { get; }
            public int Location { get; }
            public List<KeypadRecord> Keypads { get; } = new List<KeypadRecord>();
        }

        private static string NameOrUnassigned(string? name) =>
            string.IsNullOrWhiteSpace(name) ? Unassigned : name!.Trim();

        private static string ResolveRoomName(FamilyInstance fi, SpaceRoomFinderService.SpaceLookupCache roomCache)
        {
            Space? space = roomCache.FindSpace(fi);
            if (space != null) return SpaceRoomFinderService.ReadSpaceName(space);
            return roomCache.FindRoomName(fi) ?? string.Empty;
        }
    }
}
