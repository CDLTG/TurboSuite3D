using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliTabViewModel"/> — the TurboZones DALI tab (Phase 3e). Revit-free, so the
    /// grouping/assignment/warning logic and the persisted snapshot are pinned directly. Covers: persisted
    /// loops rehydrate (pool gets the remainder, single membership), the add/remove gestures move zones,
    /// the unassigned + over-cap warnings, and that a snapshot is saved on edit but never during load.
    /// </summary>
    public class DaliTabViewModelTests
    {
        /// <summary>Runs work synchronously — enough to exercise the coalesced save path deterministically.</summary>
        private sealed class SyncWorkQueue : IRevitWorkQueue
        {
            public void Enqueue(Func<object> work, Action<object> onComplete)
            {
                object result = work();
                onComplete?.Invoke(result);
            }
        }

        private sealed class CapturingStore : IDaliLoopStore
        {
            public DaliModuleState Last = new DaliModuleState();
            public int SaveCount;
            public void Save(DaliModuleState state) { Last = state; SaveCount++; }
        }

        private static DaliZoneItemViewModel Zone(string name, int loads) =>
            new DaliZoneItemViewModel(name, loads);

        private static DaliLoopDto Dto(string name, int order, int zone, params string[] zones) =>
            new DaliLoopDto { LoopId = name, Name = name, Order = order, AssignedZone = zone,
                              ZoneValues = zones.ToList() };

        private static DaliTabViewModel Build(
            IReadOnlyList<DaliZoneItemViewModel> zones,
            IReadOnlyList<int> panelZones,
            DaliModuleState? saved,
            out CapturingStore store)
        {
            store = new CapturingStore();
            return new DaliTabViewModel(zones, panelZones, saved ?? new DaliModuleState(),
                                        new SyncWorkQueue(), store);
        }

        [Fact]
        public void PersistedLoop_Rehydrates_PoolGetsRemainder()
        {
            var zones = new[] { Zone("A", 2), Zone("B", 3), Zone("C", 4) };
            var saved = new DaliModuleState { Loops = { Dto("L1", 0, 5, "A") } };

            var vm = Build(zones, new[] { 5 }, saved, out _);

            var loop = Assert.Single(vm.Loops);
            Assert.Equal("L1", loop.Name);
            Assert.Equal(5, loop.AssignedZone);
            Assert.Equal(new[] { "A" }, loop.Zones.Select(z => z.ZoneName));
            Assert.Equal(2, loop.LoadCount);
            Assert.Equal(new[] { "B", "C" }, vm.Pool.Select(z => z.ZoneName));   // remainder pooled
        }

        [Fact]
        public void ContestedZone_SticksToFirstLoop_OnLoad()
        {
            var zones = new[] { Zone("Shared", 1), Zone("Own", 1) };
            var saved = new DaliModuleState
            {
                Loops = { Dto("First", 0, 0, "Shared"), Dto("Second", 1, 0, "Shared", "Own") }
            };

            var vm = Build(zones, new int[0], saved, out _);

            Assert.Equal(new[] { "Shared" }, vm.Loops[0].Zones.Select(z => z.ZoneName));
            Assert.Equal(new[] { "Own" }, vm.Loops[1].Zones.Select(z => z.ZoneName));
            Assert.Empty(vm.Pool);
        }

        [Fact]
        public void NoSaveDuringLoad()
        {
            var vm = Build(new[] { Zone("A", 2) }, new int[0],
                           new DaliModuleState { Loops = { Dto("L", 0, 0, "A") } }, out var store);

            Assert.Equal(0, store.SaveCount);   // constructing the tab must not write
        }

        [Fact]
        public void NewLoopFromSelection_MovesSelectedZones_AndSaves()
        {
            var vm = Build(new[] { Zone("A", 2), Zone("B", 3) }, new int[0], null, out var store);
            foreach (var z in vm.Pool) z.IsSelected = true;

            vm.NewLoopFromSelectionCommand.Execute(null);

            var loop = Assert.Single(vm.Loops);
            Assert.Equal(new[] { "A", "B" }, loop.Zones.Select(z => z.ZoneName));
            Assert.Empty(vm.Pool);
            Assert.True(store.SaveCount > 0);
            Assert.Equal(2, Assert.Single(store.Last.Loops).ZoneValues.Count);
        }

        [Fact]
        public void RemoveLoop_ReturnsZonesToPool()
        {
            var zones = new[] { Zone("A", 2), Zone("B", 3) };
            var saved = new DaliModuleState { Loops = { Dto("L1", 0, 0, "A", "B") } };
            var vm = Build(zones, new int[0], saved, out _);

            vm.Loops[0].RemoveCommand!.Execute(null);

            Assert.Empty(vm.Loops);
            Assert.Equal(new[] { "A", "B" }, vm.Pool.Select(z => z.ZoneName));
        }

        [Fact]
        public void UnassignedLoopWithLoads_RaisesWarning()
        {
            var saved = new DaliModuleState { Loops = { Dto("Orphan", 0, 0, "A") } };
            var vm = Build(new[] { Zone("A", 4) }, new[] { 1 }, saved, out _);

            Assert.True(vm.HasUnassignedLoops);
            Assert.Equal(1, vm.UnassignedLoopCount);

            // Assigning a zone clears it.
            vm.Loops[0].AssignedZone = 1;
            Assert.False(vm.HasUnassignedLoops);
        }

        [Fact]
        public void EmptyLoop_IsNotCountedUnassigned()
        {
            // A declared loop with no zones has no loads, so it is neither ordered nor a "not placed" warning.
            var saved = new DaliModuleState { Loops = { Dto("Empty", 0, 0) } };
            var vm = Build(new[] { Zone("A", 4) }, new int[0], saved, out _);

            Assert.False(vm.HasUnassignedLoops);
        }

        [Fact]
        public void OverCapLoop_RaisesWarning()
        {
            var saved = new DaliModuleState { Loops = { Dto("Big", 0, 1, "A") } };
            var vm = Build(new[] { Zone("A", 70) }, new[] { 1 }, saved, out _);

            Assert.True(vm.HasOverCapLoops);
            Assert.True(vm.Loops[0].IsOverCap);
        }

        [Fact]
        public void Snapshot_CarriesNameZoneAndAssignment_AtVersion2()
        {
            var vm = Build(new[] { Zone("A", 2) }, new[] { 7 }, null, out var store);
            foreach (var z in vm.Pool) z.IsSelected = true;
            vm.NewLoopFromSelectionCommand.Execute(null);
            vm.Loops[0].Name = "Kitchen";
            vm.Loops[0].AssignedZone = 7;

            Assert.Equal(2, store.Last.PayloadVersion);
            var dto = Assert.Single(store.Last.Loops);
            Assert.Equal("Kitchen", dto.Name);
            Assert.Equal(7, dto.AssignedZone);
            Assert.Equal(new[] { "A" }, dto.ZoneValues);
        }

        [Fact]
        public void RenamedZone_DropsOnLoad_NotOrphanedIntoPool()
        {
            // Persisted loop references a zone no longer in the model — it must simply vanish, not reappear.
            var saved = new DaliModuleState { Loops = { Dto("L", 0, 0, "Gone", "Live") } };
            var vm = Build(new[] { Zone("Live", 3) }, new int[0], saved, out _);

            Assert.Equal(new[] { "Live" }, vm.Loops[0].Zones.Select(z => z.ZoneName));
            Assert.Empty(vm.Pool);
        }
    }
}
