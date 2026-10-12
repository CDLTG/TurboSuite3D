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
            public IReadOnlyList<(string Name, int Location, bool IsOverride)> LastLocations { get; private set; }
            public int SaveLocationsCalls { get; private set; }

            public void SaveRoomOrder(IReadOnlyList<(string Name, int ClickOrder)> roomOrder) { }
            public void SaveKeypadRoomSorted(bool isSorted) { }

            public void SaveRoomLocations(IReadOnlyList<(string Name, int Location, bool IsOverride)> roomLocations)
            {
                SaveLocationsCalls++;
                LastLocations = roomLocations;
            }
        }

        private static RoomLocationSeed Seed(params int[] votes) => RoomLocationSeeder.Classify(votes);

        private static (RoomOrderViewModel vm, CapturingStore store) Build(
            IEnumerable<string> rooms,
            IReadOnlyDictionary<string, RoomLocationSeed> seeds = null,
            IEnumerable<(string Name, int Location, bool IsOverride)> picks = null,
            IEnumerable<string> keypadRooms = null)
        {
            var store = new CapturingStore();
            var vm = new RoomOrderViewModel(
                rooms.ToList(),
                Array.Empty<(string, int)>(),                       // no saved order → alphabetical build
                (picks ?? Enumerable.Empty<(string, int, bool)>()).ToList(),
                seeds ?? new Dictionary<string, RoomLocationSeed>(),
                (keypadRooms ?? Enumerable.Empty<string>()).ToList(),
                new ImmediateWorkQueue(), store);
            return (vm, store);
        }

        // Convenience: a plain (non-override) persisted pick.
        private static (string, int, bool) Pick(string name, int loc) => (name, loc, false);
        // Convenience: a persisted acknowledged override.
        private static (string, int, bool) Override(string name, int loc) => (name, loc, true);

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
        public void UnanimousSeed_IsLocked_AndIgnoresStalePick()
        {
            // A unanimous seed is authoritative: a stale pick on the row is inert (the seed wins), the
            // row reads as a derived seed, and the combo is locked (CanEditLocation == false).
            var (vm, _) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) },
                picks: new[] { Pick("KITCHEN", 9) });

            var row = Row(vm, "KITCHEN");
            Assert.True(row.IsLocked);
            Assert.False(row.CanEditLocation);
            Assert.True(row.IsSeeded);
            Assert.False(row.IsExplicit);
            Assert.Equal(1, row.ResolvedLocation);   // the seed, not the stale pick
        }

        [Fact]
        public void MixedAndNoneRows_StayEditable()
        {
            // The pick's two legitimate homes: Mixed (disagreeing signals) and None (no signal).
            var (vm, _) = Build(new[] { "POWDER", "CLOSET" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["POWDER"] = Seed(1, 4) });

            Assert.True(Row(vm, "POWDER").CanEditLocation);   // mixed
            Assert.True(Row(vm, "CLOSET").CanEditLocation);   // no seed
        }

        [Fact]
        public void ExplicitPick_OnMixedRow_WinsAndIsNotGreyed()
        {
            var (vm, _) = Build(new[] { "POWDER" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["POWDER"] = Seed(1, 4) },
                picks: new[] { Pick("POWDER", 9) });

            var row = Row(vm, "POWDER");
            Assert.True(row.IsExplicit);
            Assert.False(row.IsSeeded);
            Assert.False(row.IsMixed);          // resolved → no lingering flag
            Assert.True(row.CanEditLocation);
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

            Row(vm, "B").LocationEntry = "5";    // user picks a location for B (seed-less row)

            Assert.Equal(1, store.SaveLocationsCalls);
            // Only the explicit pick is persisted — the auto-seed (A) and blank (C) are not written.
            // A seed-less pick carries IsOverride == false.
            Assert.Equal(new[] { ("B", 5, false) }, store.LastLocations.OrderBy(x => x.Name).ToArray());
        }

        [Fact]
        public void ClearingPick_RemovesItFromPersistedSet()
        {
            var (vm, store) = Build(new[] { "A" },
                picks: new[] { Pick("A", 3) });   // A is seed-less with a pick

            Row(vm, "A").LocationEntry = "";    // clear the pick

            Assert.Equal(1, store.SaveLocationsCalls);
            Assert.Empty(store.LastLocations);
        }

        // ── Override: the deliberate escape hatch on a unanimous (mis-detected) room ────────────────

        [Fact]
        public void UnlockThenDifferingValue_BecomesOverride_AndPersistsFlag()
        {
            var (vm, store) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) });
            var row = Row(vm, "KITCHEN");

            row.UnlockCommand.Execute(null);     // click the lock
            Assert.True(row.CanEditLocation);    // combo now shown
            Assert.False(row.IsOverridden);      // nothing changed yet

            row.LocationEntry = "3";             // route against the seed

            Assert.True(row.IsOverridden);
            Assert.False(row.IsSeeded);
            Assert.False(row.IsLocked);
            Assert.Equal(3, row.ResolvedLocation);           // override wins over seed 1
            Assert.Equal(new[] { ("KITCHEN", 3, true) }, store.LastLocations.ToArray());  // flagged
        }

        [Fact]
        public void PersistedOverride_SurvivesReload()
        {
            // Simulates a reopen: the store hands back a flagged override on a unanimous room.
            var (vm, _) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) },
                picks: new[] { Override("KITCHEN", 3) });

            var row = Row(vm, "KITCHEN");
            Assert.True(row.IsOverridden);
            Assert.True(row.CanEditLocation);    // not locked — it's an active override
            Assert.False(row.IsSeeded);
            Assert.Equal(3, row.ResolvedLocation);
        }

        [Fact]
        public void OverrideClearedBackToSeed_ReLocksAndDropsOverride()
        {
            var (vm, store) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) },
                picks: new[] { Override("KITCHEN", 3) });
            var row = Row(vm, "KITCHEN");

            row.LocationEntry = "";              // clear it

            Assert.True(row.IsLocked);           // back to the authoritative seed
            Assert.False(row.IsOverridden);
            Assert.Equal(1, row.ResolvedLocation);
            Assert.Empty(store.LastLocations);   // override dropped from persistence
        }

        [Fact]
        public void TypingTheSeedValueOnUnlockedRow_IsNotAnOverride()
        {
            var (vm, store) = Build(new[] { "KITCHEN" },
                seeds: new Dictionary<string, RoomLocationSeed> { ["KITCHEN"] = Seed(1) });
            var row = Row(vm, "KITCHEN");

            row.UnlockCommand.Execute(null);
            row.LocationEntry = "1";             // same as the seed → no override

            Assert.False(row.IsOverridden);
            Assert.True(row.IsLocked);           // re-locked
            Assert.Equal(1, row.ResolvedLocation);
            Assert.Equal(0, store.SaveLocationsCalls);  // nothing changed → nothing persisted
        }

        [Fact]
        public void ResolvedLocationsByRoom_FoldsPicksAndOverridesOverSeeds()
        {
            var (vm, _) = Build(new[] { "A", "B", "C", "D", "E" },
                seeds: new Dictionary<string, RoomLocationSeed>
                {
                    ["A"] = Seed(1),        // seeded 1
                    ["B"] = Seed(2, 5),     // mixed → blank
                    ["E"] = Seed(8),        // seeded 8, but overridden below
                },
                picks: new[] { Pick("C", 7), Override("E", 2) });  // C gap-fill; E deliberate override
            // D: no seed, no pick → blank (absent from the map).

            var map = vm.ResolvedLocationsByRoom;
            Assert.Equal(1, map["A"]);
            Assert.Equal(7, map["C"]);
            Assert.Equal(2, map["E"]);               // override wins over seed 8
            Assert.False(map.ContainsKey("B"));
            Assert.False(map.ContainsKey("D"));
        }
    }
}
