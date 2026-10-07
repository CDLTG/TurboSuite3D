#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;
using TurboSuite.Number.ViewModels;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Number
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Tests for the per-row LOCATION state the Room Order sidebar gained in Phase A
    //  (Core/Number/ViewModels/RoomOrderViewModel.cs). Deterministic: given injected auto-seeds,
    //  explicit picks, and keypad-room flags, each row resolves a location via the ladder and exposes
    //  the display states the view binds (greyed seed / explicit / mixed-flag / needs-assign / blank).
    //  Editing a pick persists the sparse explicit set through the store. Revit-free via fakes.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class RoomOrderLocationTests
    {
        private sealed class ImmediateWorkQueue : IRevitWorkQueue
        {
            public void Enqueue(Func<object> work, Action<object> onComplete)
            {
                var result = work();
                onComplete?.Invoke(result);
            }
        }

        private sealed class CapturingStore : IRoomOrderStore
        {
            public IReadOnlyList<(string Name, int Location)> LastLocations { get; private set; }
            public int SaveLocationsCalls { get; private set; }

            public void SaveRoomOrder(IReadOnlyList<(string Name, int ClickOrder)> roomOrder) { }
            public void SaveKeypadRoomSorted(bool isSorted) { }

            public void SaveRoomLocations(IReadOnlyList<(string Name, int Location)> roomLocations)
            {
                SaveLocationsCalls++;
                LastLocations = roomLocations;
            }
        }

        private static RoomLocationSeed Seed(params int[] votes) => RoomLocationSeeder.Classify(votes);

        private static (RoomOrderViewModel vm, CapturingStore store) Build(
            IEnumerable<string> rooms,
            IReadOnlyDictionary<string, RoomLocationSeed> seeds = null,
            IEnumerable<(string, int)> picks = null,
            IEnumerable<string> keypadRooms = null)
        {
            var store = new CapturingStore();
            var vm = new RoomOrderViewModel(
                rooms.ToList(),
                Array.Empty<(string, int)>(),                       // no saved order → alphabetical build
                (picks ?? Enumerable.Empty<(string, int)>()).ToList(),
                seeds ?? new Dictionary<string, RoomLocationSeed>(),
                (keypadRooms ?? Enumerable.Empty<string>()).ToList(),
                new ImmediateWorkQueue(), store);
            return (vm, store);
        }

        private static RoomOrderItem Row(RoomOrderViewModel vm, string name) =>
            vm.RoomOrder.First(r => r.Name == name);

        [Fact]
        public void UnanimousSeed_NoPick_IsGreyedSeed()
        {
            var (vm, _) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1, 1) });

            var row = Row(vm, "KITCHEN");
            Assert.True(row.IsSeeded);
            Assert.False(row.IsExplicit);
            Assert.False(row.IsMixed);
            Assert.Equal(1, row.ResolvedLocation);
            Assert.Equal("1", row.LocationDisplay);
        }

        [Fact]
        public void ExplicitPick_OverridesSeed_AndIsNotGreyed()
        {
            var (vm, _) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) },
                picks: new[] { ("KITCHEN", 9) });

            var row = Row(vm, "KITCHEN");
            Assert.True(row.IsExplicit);
            Assert.False(row.IsSeeded);
            Assert.Equal(9, row.ResolvedLocation);
        }

        [Fact]
        public void MixedSeed_NoPick_FlagsWithHistogramHint()
        {
            var (vm, _) = Build(new[] { "POWDER" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["POWDER"] = Seed(1, 4) });

            var row = Row(vm, "POWDER");
            Assert.True(row.IsMixed);
            Assert.Equal(0, row.ResolvedLocation);     // mixed → blank until resolved
            Assert.Equal("1/4", row.MixedHint);
        }

        [Fact]
        public void KeypadRoom_WithNoLocation_NeedsAssign()
        {
            // A keypad in a locally-switched room: no signal, no pick → the one prompted state.
            var (vm, _) = Build(new[] { "SWITCHED RM", "CLOSET" },
                keypadRooms: new[] { "SWITCHED RM" });

            Assert.True(Row(vm, "SWITCHED RM").NeedsAssign);   // keypad + blank
            Assert.False(Row(vm, "CLOSET").NeedsAssign);       // non-control room stays calm
        }

        [Fact]
        public void KeypadRoom_WithSeededLocation_DoesNotNeedAssign()
        {
            var (vm, _) = Build(new[] { "GYM" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["GYM"] = Seed(2) },
                keypadRooms: new[] { "GYM" });

            Assert.False(Row(vm, "GYM").NeedsAssign);
        }

        [Fact]
        public void EditingPick_PersistsSparseExplicitSet()
        {
            var (vm, store) = Build(new[] { "A", "B", "C" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["A"] = Seed(1) });  // A is auto-seeded

            Row(vm, "B").ExplicitLocation = 5;    // user picks a location for B

            Assert.Equal(1, store.SaveLocationsCalls);
            // Only the explicit pick is persisted — the auto-seed (A) and blank (C) are not written.
            Assert.Equal(new[] { ("B", 5) }, store.LastLocations.OrderBy(x => x.Name).ToArray());
        }

        [Fact]
        public void ClearingPick_RemovesItFromPersistedSet()
        {
            var (vm, store) = Build(new[] { "A" },
                picks: new[] { ("A", 3) });

            Row(vm, "A").ExplicitLocation = 0;    // clear the pick

            Assert.Equal(1, store.SaveLocationsCalls);
            Assert.Empty(store.LastLocations);
        }

        [Fact]
        public void ResolvedLocationsByRoom_FoldsPicksOverSeeds()
        {
            var (vm, _) = Build(new[] { "A", "B", "C", "D" },
                seeds: new Dictionary<string, RoomLocationSeed>
                {
                    ["A"] = Seed(1),        // seeded 1
                    ["B"] = Seed(2, 5),     // mixed → blank
                },
                picks: new[] { ("C", 7) });  // explicit 7
            // D: no seed, no pick → blank (absent from the map).

            var map = vm.ResolvedLocationsByRoom;
            Assert.Equal(1, map["A"]);
            Assert.Equal(7, map["C"]);
            Assert.False(map.ContainsKey("B"));
            Assert.False(map.ContainsKey("D"));
        }
    }
}
