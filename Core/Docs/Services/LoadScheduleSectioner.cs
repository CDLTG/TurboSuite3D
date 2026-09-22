using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Pure grouping of Load Schedule circuits into room sections for the By-Room export. Revit-free
/// and oracle-tested; the shim stamps each circuit's <see cref="LoadsCircuitModel.RoomName"/> and
/// loads the project-wide Room Order, then hands both here.
///
/// Section order (mirrors <c>RoomOrderPanelSorter</c>'s "listed rooms, then the rest" grain):
///   1. Rooms present in <paramref name="roomOrder"/>, in that order.
///   2. Resolved-but-unordered rooms (a real room name not in the current order — staleness),
///      alpha, after all ordered rooms. Rendered identically to an ordered room.
///   3. A single trailing "(No Room)" section for circuits whose room is blank/unresolved.
/// Within every section, circuits sort by natural circuit number — the same key the By-Circuit
/// export uses, so the two modes never disagree on intra-room ordering.
/// </summary>
public static class LoadScheduleSectioner
{
    public const string NoRoomLabel = "(No Room)";

    public static List<LoadSection> Section(
        IReadOnlyList<LoadsCircuitModel> circuits,
        IReadOnlyList<string> roomOrder)
    {
        var sections = new List<LoadSection>();
        if (circuits == null || circuits.Count == 0) return sections;

        // Rank from the Room Order (list position is authoritative — see RoomOrderViewModel seed).
        // First occurrence of a name wins; blank entries and later duplicates are ignored.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (roomOrder != null)
        {
            for (int i = 0; i < roomOrder.Count; i++)
            {
                string name = roomOrder[i]?.Trim() ?? string.Empty;
                if (name.Length > 0 && !rank.ContainsKey(name))
                    rank[name] = i;
            }
        }

        // Group by resolved room (case-insensitive). Blank/whitespace room → the trailing bucket.
        // The group's display name is the first-seen casing of that room.
        var noRoom = new List<LoadsCircuitModel>();
        var groups = new Dictionary<string, (string Display, List<LoadsCircuitModel> Circuits)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in circuits)
        {
            if (c == null) continue;
            string room = c.RoomName?.Trim() ?? string.Empty;
            if (room.Length == 0)
            {
                noRoom.Add(c);
                continue;
            }
            if (!groups.TryGetValue(room, out var g))
            {
                g = (room, new List<LoadsCircuitModel>());
                groups[room] = g;
            }
            g.Circuits.Add(c);
        }

        var ordered = groups.Values
            .Where(g => rank.ContainsKey(g.Display))
            .OrderBy(g => rank[g.Display]);
        var unordered = groups.Values
            .Where(g => !rank.ContainsKey(g.Display))
            .OrderBy(g => g.Display, StringComparer.OrdinalIgnoreCase);

        foreach (var g in ordered.Concat(unordered))
            sections.Add(new LoadSection(g.Display, false, SortWithin(g.Circuits)));

        if (noRoom.Count > 0)
            sections.Add(new LoadSection(NoRoomLabel, true, SortWithin(noRoom)));

        return sections;
    }

    private static List<LoadsCircuitModel> SortWithin(List<LoadsCircuitModel> circuits) =>
        circuits
            .OrderBy(c => c.CircuitNumber, NaturalStringComparer.OrdinalIgnoreCase)
            .ToList();
}
