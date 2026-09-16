using System.Collections.Generic;
using System.Linq;
using TurboSuite.Number.Services;
using Xunit;
using static TurboSuite.Number.Services.RoomOrderPanelSorter;

namespace TurboSuite.Tests.Number
{
    /// <summary>
    /// Oracle suite for <see cref="RoomOrderPanelSorter.ComputeSwaps"/>: the pure engine
    /// behind the CircuitNumber "Sort by room order". Circuits compact to the top in room
    /// order (stable within a room); every non-circuit slot sinks below. Swaps are applied
    /// here exactly as the shim applies them — cell-content exchanges over positionally
    /// stable cells — so a green test means the emitted position pairs reproduce the target.
    /// </summary>
    public class RoomOrderPanelSorterTests
    {
        // A slot: circuit rows carry a room name; non-circuit rows carry their kind label
        // (Empty/Spare/Space) purely so the assertions can read the resulting order.
        private static SlotItem Circuit(string room) => new SlotItem(true, room);
        private static SlotItem NonCircuit() => new SlotItem(false, "");

        // Apply the emitted (A,B) position swaps to a parallel label array, mirroring the
        // shim's MoveSlotTo loop, and return the resulting top-to-bottom labels.
        private static List<string> ApplyAndLabel(SlotItem[] slots, string[] labels,
            IReadOnlyList<string> roomOrder)
        {
            var swaps = ComputeSwaps(slots, roomOrder);
            var result = (string[])labels.Clone();
            foreach (var (a, b) in swaps)
                (result[a], result[b]) = (result[b], result[a]);
            return result.ToList();
        }

        [Fact]
        public void ReordersCircuitsIntoRoomOrder()
        {
            var slots = new[] { Circuit("Kitchen"), Circuit("Foyer"), Circuit("Bath") };
            var labels = new[] { "Kitchen", "Foyer", "Bath" };
            var roomOrder = new[] { "Foyer", "Kitchen", "Bath" };

            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "Foyer", "Kitchen", "Bath" }, result);
        }

        [Fact]
        public void StableWithinSameRoom()
        {
            // Three circuits in "Foyer" keep their current relative order (A,B,C), even
            // though they're pulled above the Kitchen circuit.
            var slots = new[] { Circuit("Kitchen"), Circuit("Foyer"), Circuit("Foyer"), Circuit("Foyer") };
            var labels = new[] { "K", "FoyerA", "FoyerB", "FoyerC" };
            var roomOrder = new[] { "Foyer", "Kitchen" };

            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "FoyerA", "FoyerB", "FoyerC", "K" }, result);
        }

        [Fact]
        public void NoRoomCircuitsSinkBelowListedRoomsButAboveNonCircuits()
        {
            var slots = new[] { Circuit("Attic"), Circuit("Foyer"), NonCircuit(), Circuit("") };
            var labels = new[] { "Attic", "Foyer", "Empty", "Blank" };
            var roomOrder = new[] { "Foyer" }; // Attic + blank are unlisted

            var result = ApplyAndLabel(slots, labels, roomOrder);

            // Foyer (listed) first; then unlisted circuits Attic & Blank in original order;
            // then the non-circuit.
            Assert.Equal(new[] { "Foyer", "Attic", "Blank", "Empty" }, result);
        }

        [Fact]
        public void EmptySpareSpaceAllSinkToBottomInOriginalOrder()
        {
            var slots = new[]
            {
                NonCircuit(), Circuit("Foyer"), NonCircuit(), Circuit("Kitchen"), NonCircuit()
            };
            var labels = new[] { "Empty1", "Foyer", "Spare", "Kitchen", "Space" };
            var roomOrder = new[] { "Foyer", "Kitchen" };

            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "Foyer", "Kitchen", "Empty1", "Spare", "Space" }, result);
        }

        [Fact]
        public void ListedRoomsWithNoCircuitsAreSkipped()
        {
            var slots = new[] { Circuit("Bath"), Circuit("Foyer") };
            var labels = new[] { "Bath", "Foyer" };
            var roomOrder = new[] { "Foyer", "Hallway", "Bath" }; // Hallway has no circuit

            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "Foyer", "Bath" }, result);
        }

        [Fact]
        public void EmptyRoomOrderPreservesCircuitOrderAndSinksNonCircuits()
        {
            var slots = new[] { Circuit("Kitchen"), NonCircuit(), Circuit("Foyer") };
            var labels = new[] { "Kitchen", "Empty", "Foyer" };

            var result = ApplyAndLabel(slots, labels, new string[0]);

            // No ranking ⇒ circuits keep original order, non-circuit to bottom.
            Assert.Equal(new[] { "Kitchen", "Foyer", "Empty" }, result);
        }

        [Fact]
        public void AlreadySortedInputYieldsNoSwaps()
        {
            var slots = new[] { Circuit("Foyer"), Circuit("Kitchen"), NonCircuit(), NonCircuit() };
            var roomOrder = new[] { "Foyer", "Kitchen" };

            var swaps = ComputeSwaps(slots, roomOrder);

            Assert.Empty(swaps);
        }

        [Fact]
        public void GapAmongCircuitsEmitsCrossTypeSwapPullingCircuitUp()
        {
            // Empty sits between two circuits; sorting must pull the lower circuit up into
            // the empty's position band and push the empty to the bottom — a circuit<->empty
            // (cross-type) exchange, which the shim proved valid after clearing spares.
            var slots = new[] { Circuit("Foyer"), NonCircuit(), Circuit("Kitchen") };
            var labels = new[] { "Foyer", "Empty", "Kitchen" };
            var roomOrder = new[] { "Foyer", "Kitchen" };

            var swaps = ComputeSwaps(slots, roomOrder);
            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "Foyer", "Kitchen", "Empty" }, result);
            Assert.Single(swaps);
            Assert.Contains((1, 2), swaps); // exchange position 1 (Empty) with 2 (Kitchen)
        }

        [Fact]
        public void RoomMatchingIsCaseInsensitive()
        {
            var slots = new[] { Circuit("KITCHEN"), Circuit("foyer") };
            var labels = new[] { "KITCHEN", "foyer" };
            var roomOrder = new[] { "Foyer", "Kitchen" };

            var result = ApplyAndLabel(slots, labels, roomOrder);

            Assert.Equal(new[] { "foyer", "KITCHEN" }, result);
        }

        [Fact]
        public void ReversalUsesMinimalSwaps()
        {
            // 6 circuits fully reversed ⇒ 3 exchanges (n/2), matching the spike's 27→13.
            var rooms = new[] { "R1", "R2", "R3", "R4", "R5", "R6" };
            var slots = rooms.Reverse().Select(Circuit).ToArray(); // R6..R1
            var swaps = ComputeSwaps(slots, rooms);                // want R1..R6

            Assert.Equal(3, swaps.Count);
        }
    }
}
