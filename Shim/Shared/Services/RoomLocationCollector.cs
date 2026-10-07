#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.Services;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Derives each room's auto-seed location (Phase A of the located-keypads plan) from the two
    /// signals <c>RoomLocationSeeder</c> expects, both read from state the project already carries:
    /// <list type="number">
    /// <item><b>paneled lighting</b> — every lighting fixture's power-circuit panel name (the
    /// name-prefix "1-A" → Location 1);</item>
    /// <item><b>DALI lighting</b> — every DALI fixture's "Control Zone" value, matched to the declared
    /// loop that owns it (loops + their <c>AssignedZone</c> come from <c>DaliStorageService</c>).</item>
    /// </list>
    /// Fixture-grain: each lighting fixture in a room contributes one vote per signal. Because the
    /// classification is on the <i>distinct</i> non-zero votes, fixture-grain yields the same
    /// unanimous/mixed/none verdict as circuit-grain (only the histogram counts differ). Rooms resolve
    /// through the same <see cref="SpaceRoomFinderService.SpaceLookupCache"/> used elsewhere, so this
    /// works in both 3D (Spaces) and 2D (Room Regions). Shared by TurboNumber's sidebar (which reads
    /// the <see cref="Collect"/> seeds for its per-row states) and TurboZones' keypad collector (which
    /// reads the <see cref="ResolveLocations"/> folded map to locate keypads).
    /// </summary>
    public sealed class RoomLocationCollector
    {
        /// <summary>Per-room auto-seed classification (unanimous/mixed/none), keyed by room name.
        /// Only rooms that produced at least one signal appear; others are location-less (blank).</summary>
        public IReadOnlyDictionary<string, RoomLocationSeed> Collect(Document doc)
        {
            var result = new Dictionary<string, RoomLocationSeed>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return result;

            var regionFallback = new RegionRoomLookupService(doc);
            var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);

            // DALI loops as plain tuples (ZoneValues → AssignedZone), so Core/Zones stays DTO-free.
            // Load is total; a job that never ran TurboDALI yields no loops → the DALI signal is silent.
            var daliState = DaliStorageService.Load(doc);
            var loops = (daliState?.Loops ?? new List<Dali.Persistence.DaliLoopDto>())
                .Select(l => ((IReadOnlyList<string>)(l.ZoneValues ?? new List<string>()), l.AssignedZone))
                .ToList();

            var panelNamesByRoom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var zoneValuesByRoom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in fixtures)
            {
                string room = ResolveRoomName(fi, roomCache);
                if (string.IsNullOrEmpty(room)) continue;

                // (1) paneled signal — the fixture's power circuit's panel name.
                var circuit = fi.MEPModel?.GetElectricalSystems()?.FirstOrDefault();
                string panel = circuit != null ? ParameterHelper.GetPanelName(circuit) : null;
                if (!string.IsNullOrEmpty(panel))
                    Add(panelNamesByRoom, room, panel);

                // (2) DALI signal — a DALI fixture's Control Zone value.
                string protocol = ParameterHelper.GetDimmingProtocol(fi);
                if (string.Equals(protocol, "DALI", StringComparison.OrdinalIgnoreCase))
                {
                    string zone = fi.LookupParameter(ParameterNames.ControlZone)?.AsString();
                    if (!string.IsNullOrWhiteSpace(zone))
                        Add(zoneValuesByRoom, room, zone);
                }
            }

            var rooms = new HashSet<string>(panelNamesByRoom.Keys, StringComparer.OrdinalIgnoreCase);
            rooms.UnionWith(zoneValuesByRoom.Keys);

            foreach (var room in rooms)
            {
                var panelVotes = panelNamesByRoom.TryGetValue(room, out var pn)
                    ? RoomLocationSeeder.PanelVotes(pn)
                    : Enumerable.Empty<int>();
                var daliVotes = zoneValuesByRoom.TryGetValue(room, out var zv)
                    ? RoomLocationSeeder.DaliVotes(zv, loops)
                    : Enumerable.Empty<int>();

                result[room] = RoomLocationSeeder.Classify(panelVotes, daliVotes);
            }

            return result;
        }

        /// <summary>
        /// The effective room→location map, folding the designer's explicit picks
        /// (<c>RoomLocationStorageService</c>) over the auto-seeds via the resolution ladder. Only rooms
        /// that resolve to a positive location appear. This is what TurboZones reads to locate keypads —
        /// the same value the TurboNumber sidebar shows per row.
        /// </summary>
        public IReadOnlyDictionary<string, int> ResolveLocations(Document doc)
        {
            var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return resolved;

            var seeds = Collect(doc);
            var picks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, loc) in RoomLocationStorageService.Load(doc))
                if (!string.IsNullOrEmpty(name) && loc > 0) picks[name] = loc;

            var empty = RoomLocationSeeder.Classify(Array.Empty<int>());
            var rooms = new HashSet<string>(seeds.Keys, StringComparer.OrdinalIgnoreCase);
            rooms.UnionWith(picks.Keys);

            foreach (var room in rooms)
            {
                int pick = picks.TryGetValue(room, out int p) ? p : 0;
                var seed = seeds.TryGetValue(room, out var s) ? s : empty;
                int location = RoomLocationSeeder.Resolve(pick, seed);
                if (location > 0) resolved[room] = location;
            }

            return resolved;
        }

        private static string ResolveRoomName(FamilyInstance fi, SpaceRoomFinderService.SpaceLookupCache roomCache)
        {
            Space space = roomCache.FindSpace(fi);
            if (space != null) return SpaceRoomFinderService.ReadSpaceName(space);
            return roomCache.FindRoomName(fi) ?? "";
        }

        private static void Add(Dictionary<string, List<string>> map, string room, string value)
        {
            if (!map.TryGetValue(room, out var list))
                map[room] = list = new List<string>();
            list.Add(value);
        }
    }
}
