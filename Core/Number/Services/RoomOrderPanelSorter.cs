using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Pure, Revit-free computation of the slot swaps that reorder a single-column panel's
    /// circuits into the project-wide room order, compacting real circuits to the top and
    /// sinking every non-circuit slot (Empty / Spare / Space) below them.
    ///
    /// Input <see cref="SlotItem"/>s are in current top-to-bottom order. Output is a list
    /// of <c>(A, B)</c> <b>position-index</b> pairs into that same array — each pair means
    /// "exchange the slot currently at position A with the one at position B". The shim
    /// maps each index back to its cell's Row/Col and drives <c>MoveSlotTo</c>; because the
    /// physical cells stay positionally stable across swaps, the indices remain valid
    /// throughout without re-reading the layout.
    ///
    /// The ordering is stable: circuits sort by (room rank, original index), so circuits in
    /// the same room keep their current relative order, and circuits whose room is not in
    /// the order list (including blank-room circuits) sink below all listed-room circuits
    /// but above every non-circuit. Already-sorted input yields an empty list (idempotent).
    /// </summary>
    public static class RoomOrderPanelSorter
    {
        public readonly struct SlotItem
        {
            public bool IsCircuit { get; }
            public string RoomName { get; }

            public SlotItem(bool isCircuit, string roomName)
            {
                IsCircuit = isCircuit;
                RoomName = roomName ?? string.Empty;
            }
        }

        public static List<(int A, int B)> ComputeSwaps(
            IReadOnlyList<SlotItem> slots, IReadOnlyList<string> roomOrder)
        {
            var swaps = new List<(int A, int B)>();
            if (slots == null || slots.Count == 0) return swaps;
            int n = slots.Count;

            var roomRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (roomOrder != null)
            {
                for (int i = 0; i < roomOrder.Count; i++)
                {
                    string name = roomOrder[i] ?? string.Empty;
                    if (!roomRank.ContainsKey(name))
                        roomRank[name] = i;
                }
            }

            int Rank(int idx) =>
                roomRank.TryGetValue(slots[idx].RoomName, out int r) ? r : int.MaxValue;

            // Target position sequence (original indices): circuits by (room rank, original
            // index) — OrderBy is stable, so equal keys preserve current order — then every
            // non-circuit slot in its original order (compact-to-top).
            var circuitTargets = Enumerable.Range(0, n)
                .Where(i => slots[i].IsCircuit)
                .OrderBy(Rank)
                .ThenBy(i => i);
            var nonCircuitTargets = Enumerable.Range(0, n)
                .Where(i => !slots[i].IsCircuit);
            var target = circuitTargets.Concat(nonCircuitTargets).ToArray();

            // Selection sort of the identity permutation into `target`, tracking cell
            // contents (loc) and each original index's live position (pos). Emits ≤ n-1
            // position swaps; identity target ⇒ none.
            var loc = Enumerable.Range(0, n).ToArray();
            var pos = Enumerable.Range(0, n).ToArray();

            for (int k = 0; k < n; k++)
            {
                int want = target[k];
                int cur = pos[want];
                if (cur == k) continue;

                int displaced = loc[k];
                swaps.Add((k, cur));

                loc[k] = want;
                loc[cur] = displaced;
                pos[want] = k;
                pos[displaced] = cur;
            }

            return swaps;
        }
    }
}
